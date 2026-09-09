using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the evidence the credential store requires about the Bootstrap file: only a proven
/// absence authorizes an issuance, and an existence the store could not establish closes both the
/// provisioning and the status observation instead of being read as "not configured".
/// </summary>
public sealed class BootstrapCredentialExistenceTests
{
    private static readonly ServiceId Service = ServiceId.Parse("credential-worker");

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bootstrap_file_that_opens_refuses_provisioning_and_is_reported_as_configured()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var clock = new FixedClock(Now);
        var store = Create(paths, clock);
        var existing = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        await File.WriteAllTextAsync(paths.Bootstrap, "{}", Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);

        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.True(existing.IsProvisioned);
        Assert.False(provisioned.IsProvisioned);
        Assert.Null(provisioned.Credential);
        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.BootstrapConfigured,
            provisioned.ErrorCode);
        // The refused provisioning left the credential record untouched.
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));

        Assert.True(status.BootstrapConfigured);
        Assert.Equal(BootstrapCredentialStatus.Provisioned, status.Status);
        Assert.Equal(Now.UtcDateTime, status.IssuedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bootstrap_file_the_open_reports_as_missing_provisions_normally(
        bool missingDirectory)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        if (missingDirectory)
        {
            // A directory that is not there is proof of absence just as a missing file is.
            paths = paths with
            {
                Bootstrap = Path.Combine(directory.Path, "absent", "signacore.bootstrap.json")
            };
        }

        var store = Create(paths, new FixedClock(Now));

        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.True(provisioned.IsProvisioned);
        Assert.Equal(BootstrapCredentialStatus.Provisioned, status.Status);
        Assert.False(status.BootstrapConfigured);
        Assert.Equal(Now.UtcDateTime, status.IssuedAtUtc);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("io")]
    public async Task An_unestablished_bootstrap_file_never_issues_a_credential(string failure)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        // A valid, unexpired record already exists, so a status that reported the credential would
        // be visibly different from one that closed the failure without reading it.
        var provisioned = await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default,
            Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var store = Create(paths, new FixedClock(Now), FailingProbe(failure));

        var refused = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.True(provisioned.IsProvisioned);
        Assert.False(refused.IsProvisioned);
        Assert.Null(refused.Credential);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, refused.ErrorCode);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));

        Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
        Assert.False(status.BootstrapConfigured);
        Assert.Null(status.IssuedAtUtc);
        Assert.Null(status.ExpiresAtUtc);
    }

    [Fact]
    public async Task A_directory_at_the_bootstrap_path_is_not_evidence_of_absence()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        // A path that cannot be opened as a file at all, through the real production open.
        Directory.CreateDirectory(paths.Bootstrap);
        var store = Create(paths, new FixedClock(Now));

        var refused = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.False(refused.IsProvisioned);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, refused.ErrorCode);
        Assert.False(File.Exists(paths.Credential));
        Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
        Assert.False(status.BootstrapConfigured);
        Assert.Null(status.IssuedAtUtc);
    }

    /// <summary>
    /// The originally reported reproduction: the Bootstrap file is present, its parent directory
    /// cannot be traversed, and the credential directory is writable elsewhere.
    /// </summary>
    [Fact]
    public async Task An_unreadable_bootstrap_parent_directory_is_not_evidence_of_absence_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            // Unix permission modes have no Windows equivalent; the Windows half of this
            // classification is the sharing refusal below.
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var bootstrapDirectory = Path.Combine(directory.Path, "bootstrap");
        Directory.CreateDirectory(bootstrapDirectory);
        var paths = new Paths(
            Path.Combine(directory.Path, "credential", "credential.json"),
            Path.Combine(bootstrapDirectory, "signacore.bootstrap.json"));
        await File.WriteAllTextAsync(paths.Bootstrap, "{}", Token);
        var bootstrapBytes = await File.ReadAllBytesAsync(paths.Bootstrap, Token);

        File.SetUnixFileMode(bootstrapDirectory, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(
                CanOpen(paths.Bootstrap),
                "The current user bypasses the directory mode, so no access failure can be observed.");

            var store = Create(paths, new FixedClock(Now));

            var refused = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
            var status = await store.GetStatusAsync(Token);

            Assert.False(refused.IsProvisioned);
            Assert.Null(refused.Credential);
            Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, refused.ErrorCode);
            Assert.False(File.Exists(paths.Credential));
            Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
            Assert.False(status.BootstrapConfigured);
            Assert.Null(status.IssuedAtUtc);
        }
        finally
        {
            File.SetUnixFileMode(
                bootstrapDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The probe reads nothing and writes nothing.
        Assert.Equal(bootstrapBytes, await File.ReadAllBytesAsync(paths.Bootstrap, Token));
    }

    [Fact]
    public async Task A_bootstrap_file_held_without_sharing_is_not_evidence_of_absence_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // A Unix open ignores other handles, so the refusal cannot be constructed there.
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        await File.WriteAllTextAsync(paths.Bootstrap, "{}", Token);
        // A real exclusive handle, so the production open is refused deterministically rather than
        // through a simulated failure.
        using var exclusive = new FileStream(
            paths.Bootstrap,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var store = Create(paths, new FixedClock(Now));

        var refused = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.False(refused.IsProvisioned);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, refused.ErrorCode);
        Assert.False(File.Exists(paths.Credential));
        Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
        Assert.False(status.BootstrapConfigured);
        Assert.Null(status.IssuedAtUtc);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("denied")]
    public async Task Cancellation_observed_at_the_probe_propagates_the_original_token(string outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        using var abort = new CancellationTokenSource();
        var store = Create(paths, new FixedClock(Now), path =>
        {
            // The caller cancels while the probe is in flight; the probe's own answer must not win
            // over the caller's token.
            abort.Cancel();
            return outcome == "absent"
                ? BootstrapFilePresenceProbe.Open(path)
                : throw new UnauthorizedAccessException("denied");
        });

        var provision = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ProvisionAsync(BootstrapCredentialLifetime.Default, abort.Token));
        var status = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.GetStatusAsync(abort.Token));

        Assert.Equal(abort.Token, provision.CancellationToken);
        Assert.Equal(abort.Token, status.CancellationToken);
        Assert.False(File.Exists(paths.Credential));
    }

    [Fact]
    public async Task The_probe_leaves_the_bootstrap_file_unchanged()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        await File.WriteAllTextAsync(paths.Bootstrap, """{"MasterKey":"secret"}""", Token);
        var before = await File.ReadAllBytesAsync(paths.Bootstrap, Token);
        var modeBefore = OperatingSystem.IsWindows()
            ? default
            : File.GetUnixFileMode(paths.Bootstrap);
        var store = Create(paths, new FixedClock(Now));

        var refused = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var status = await store.GetStatusAsync(Token);

        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.BootstrapConfigured,
            refused.ErrorCode);
        Assert.True(status.BootstrapConfigured);
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.Bootstrap, Token));
        Assert.DoesNotContain("secret", status.ToString(), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(modeBefore, File.GetUnixFileMode(paths.Bootstrap));
        }
    }

    private static Func<string, FileStream> FailingProbe(string failure) => failure switch
    {
        "denied" => _ => throw new UnauthorizedAccessException("denied"),
        _ => _ => throw new IOException("io"),
    };

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = BootstrapFilePresenceProbe.Open(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static BootstrapCredentialFileStore Create(
        Paths paths,
        TimeProvider timeProvider,
        Func<string, FileStream>? openBootstrapProbe = null) => new(
            Service,
            paths.Credential,
            paths.Bootstrap,
            timeProvider,
            openBootstrapProbe ?? BootstrapFilePresenceProbe.Open);

    private sealed record Paths(string Credential, string Bootstrap)
    {
        internal static Paths For(TemporaryDirectory directory) => new(
            Path.Combine(directory.Path, "credential.json"),
            Path.Combine(directory.Path, "signacore.bootstrap.json"));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

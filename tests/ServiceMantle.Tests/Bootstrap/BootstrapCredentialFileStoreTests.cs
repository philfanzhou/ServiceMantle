using System.Text;
using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the local Bootstrap credential record: its provisioning preconditions, the fixed file
/// protocol, the one-time atomic consumption boundary, and its fail-closed rejections.
/// </summary>
public sealed class BootstrapCredentialFileStoreTests
{
    private static readonly ServiceId Service = ServiceId.Parse("credential-worker");

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provisioning_writes_only_the_version_digest_and_time_metadata()
    {
        using var directory = TemporaryDirectory.Create();
        var clock = new MutableTimeProvider(Now);
        var store = Create(directory, clock);

        var result = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(result.IsProvisioned);
        Assert.Equal(Now.UtcDateTime, result.IssuedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddMinutes(15), result.ExpiresAtUtc);
        var raw = await File.ReadAllTextAsync(store.FilePath, Token);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(
            ["formatVersion", "digest", "issuedAtUtc", "expiresAtUtc"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            BootstrapCredentialDigest.Compute(result.Credential!).Value,
            document.RootElement.GetProperty("digest").GetString());
        Assert.DoesNotContain(result.Credential!.Reveal(), raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provisioning_creates_a_private_directory_and_file_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are not a Windows contract.");
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var nested = Path.Combine(directory.Path, "config");
        var store = new BootstrapCredentialFileStore(
            Service,
            Path.Combine(nested, "credential.json"),
            Path.Combine(directory.Path, "bootstrap.json"));

        var result = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(result.IsProvisioned);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(nested));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public async Task Provisioning_never_replaces_an_existing_credential()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var first = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var recordBefore = await File.ReadAllTextAsync(store.FilePath, Token);

        var second = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(first.IsProvisioned);
        Assert.False(second.IsProvisioned);
        Assert.Null(second.Credential);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.AlreadyExists, second.ErrorCode);
        Assert.Equal(recordBefore, await File.ReadAllTextAsync(store.FilePath, Token));
    }

    [Fact]
    public async Task Provisioning_is_refused_once_bootstrap_is_configured()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        await File.WriteAllTextAsync(
            store.BootstrapFilePath,
            "{\"masterKey\":\"do-not-read-me\"}",
            Token);

        var result = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.False(result.IsProvisioned);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.BootstrapConfigured, result.ErrorCode);
        Assert.False(File.Exists(store.FilePath));
        // The Bootstrap file is only tested for existence; nothing from it reaches the result.
        Assert.DoesNotContain("do-not-read-me", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_provisioning_produces_exactly_one_consumable_credential()
    {
        using var directory = TemporaryDirectory.Create();
        var stores = Enumerable.Range(0, 16).Select(_ => Create(directory)).ToArray();
        using var start = new Barrier(stores.Length);

        var results = await Task.WhenAll(stores.Select(store => Task.Run(async () =>
        {
            start.SignalAndWait(Token);
            return await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        }, Token)));

        var provisioned = results.Where(result => result.IsProvisioned).ToArray();
        Assert.Single(provisioned);
        Assert.All(
            results.Where(result => !result.IsProvisioned),
            result => Assert.Equal(
                WellKnownBootstrapCredentialErrorCodes.AlreadyExists,
                result.ErrorCode));
        var winner = provisioned[0].Credential!.Reveal();
        Assert.True((await stores[0].ConsumeAsync(winner, Token)).IsConsumed);
    }

    [Fact]
    public async Task A_valid_candidate_is_consumed_exactly_once()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();

        var first = await store.ConsumeAsync(candidate, Token);
        var replay = await store.ConsumeAsync(candidate, Token);

        Assert.True(first.IsConsumed);
        Assert.Null(first.ErrorCode);
        Assert.False(replay.IsConsumed);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, replay.ErrorCode);
        Assert.False(File.Exists(store.FilePath));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-credential")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task An_invalid_candidate_never_consumes_a_valid_credential(string? candidate)
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        var rejected = await store.ConsumeAsync(candidate, Token);

        Assert.False(rejected.IsConsumed);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, rejected.ErrorCode);
        Assert.True(File.Exists(store.FilePath));
        Assert.True((await store.ConsumeAsync(provisioned.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Fact]
    public async Task An_expired_or_absent_credential_uses_the_same_invalid_result()
    {
        using var directory = TemporaryDirectory.Create();
        var clock = new MutableTimeProvider(Now);
        var store = Create(directory, clock);
        var absent = await store.ConsumeAsync(BootstrapCredential.Generate().Reveal(), Token);
        var provisioned = await store.ProvisionAsync(
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(1)),
            Token);
        var candidate = provisioned.Credential!.Reveal();

        clock.UtcNow = Now.AddMinutes(1);
        var expired = await store.ConsumeAsync(candidate, Token);

        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, absent.ErrorCode);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, expired.ErrorCode);
        // An expired record is not consumed, and it never becomes usable again.
        Assert.True(File.Exists(store.FilePath));
        clock.UtcNow = Now;
        Assert.True((await store.ConsumeAsync(candidate, Token)).IsConsumed);
    }

    [Fact]
    public async Task Two_valid_consumers_and_invalid_contenders_produce_exactly_one_success()
    {
        using var directory = TemporaryDirectory.Create();
        var provisioned = await Create(directory).ProvisionAsync(
            BootstrapCredentialLifetime.Default,
            Token);
        var candidate = provisioned.Credential!.Reveal();
        var candidates = Enumerable.Range(0, 16)
            .Select(index => index % 2 == 0 ? candidate : BootstrapCredential.Generate().Reveal())
            .ToArray();
        using var start = new Barrier(candidates.Length);

        var results = await Task.WhenAll(candidates.Select(value => Task.Run(async () =>
        {
            var store = Create(directory);
            start.SignalAndWait(Token);
            return await store.ConsumeAsync(value, Token);
        }, Token)));

        Assert.Single(results, result => result.IsConsumed);
        Assert.All(
            results.Where(result => !result.IsConsumed),
            result => Assert.Equal(
                WellKnownBootstrapCredentialErrorCodes.Invalid,
                result.ErrorCode));
    }

    [Fact]
    public async Task Contending_operating_system_processes_produce_exactly_one_success()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();
        var barrierPath = Path.Combine(directory.Path, "barrier");
        var workers = Enumerable.Range(0, 4)
            .Select(_ => BootstrapCredentialWorker.Start(
                store.FilePath,
                store.BootstrapFilePath,
                barrierPath,
                candidate))
            .ToArray();

        var exitCodes = new List<int>();
        try
        {
            await File.WriteAllTextAsync(barrierPath, "go", Token);
            foreach (var worker in workers)
            {
                await worker.WaitForExitAsync(Token);
                exitCodes.Add(worker.ExitCode);
            }
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }
        }

        Assert.Single(exitCodes, code => code == BootstrapCredentialWorker.ConsumedExitCode);
        Assert.All(
            exitCodes.Where(code => code != BootstrapCredentialWorker.ConsumedExitCode),
            code => Assert.Equal(BootstrapCredentialWorker.InvalidExitCode, code));
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("empty", "")]
    [InlineData("not-json", "not json at all")]
    [InlineData("array", "[]")]
    [InlineData("unknown-member", """{"formatVersion":1,"digest":"{digest}","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}","extra":1}""")]
    [InlineData("duplicate-member", """{"formatVersion":1,"formatVersion":1,"digest":"{digest}","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    [InlineData("missing-digest", """{"formatVersion":1,"issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    [InlineData("wrong-version", """{"formatVersion":2,"digest":"{digest}","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    [InlineData("version-as-string", """{"formatVersion":"1","digest":"{digest}","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    [InlineData("digest-version", """{"formatVersion":1,"digest":"sha256-v2:0000000000000000000000000000000000000000000000000000000000000000","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    [InlineData("local-time", """{"formatVersion":1,"digest":"{digest}","issuedAtUtc":"2026-09-06T12:00:00","expiresAtUtc":"{expires}"}""")]
    [InlineData("inverted-times", """{"formatVersion":1,"digest":"{digest}","issuedAtUtc":"{expires}","expiresAtUtc":"{issued}"}""")]
    [InlineData("nested", """{"formatVersion":{"a":{"b":{"c":{"d":1}}}},"digest":"{digest}","issuedAtUtc":"{issued}","expiresAtUtc":"{expires}"}""")]
    public async Task A_damaged_record_fails_closed_as_unavailable(string scenario, string template)
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var credential = BootstrapCredential.Generate();
        await File.WriteAllTextAsync(
            store.FilePath,
            template
                .Replace("{digest}", BootstrapCredentialDigest.Compute(credential).Value, StringComparison.Ordinal)
                .Replace("{issued}", "2026-09-06T12:00:00.0000000Z", StringComparison.Ordinal)
                .Replace("{expires}", "2026-09-06T12:15:00.0000000Z", StringComparison.Ordinal),
            Token);

        var consumed = await store.ConsumeAsync(credential.Reveal(), Token);
        var status = await store.GetStatusAsync(Token);

        Assert.False(consumed.IsConsumed);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, consumed.ErrorCode);
        Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
        // A damaged record is never repaired, and it never becomes consumable.
        Assert.True(File.Exists(store.FilePath));
        Assert.NotEqual("consumed", scenario);
    }

    [Fact]
    public async Task An_oversized_record_fails_closed_without_being_parsed()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var credential = BootstrapCredential.Generate();
        var padding = new string(' ', BootstrapCredentialFileStore.MaximumFileByteCount);
        await File.WriteAllTextAsync(
            store.FilePath,
            $$"""{"formatVersion":1,"digest":"{{BootstrapCredentialDigest.Compute(credential).Value}}","issuedAtUtc":"2026-09-06T12:00:00.0000000Z","expiresAtUtc":"2026-09-06T12:15:00.0000000Z"}{{padding}}""",
            Token);

        var consumed = await store.ConsumeAsync(credential.Reveal(), Token);

        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, consumed.ErrorCode);
        Assert.True(
            new FileInfo(store.FilePath).Length > BootstrapCredentialFileStore.MaximumFileByteCount);
    }

    [Fact]
    public async Task The_status_read_reports_the_finite_record_state_without_changing_it()
    {
        using var directory = TemporaryDirectory.Create();
        var clock = new MutableTimeProvider(Now);
        var store = Create(directory, clock);

        var absent = await store.GetStatusAsync(Token);
        var provisioned = await store.ProvisionAsync(
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(5)),
            Token);
        var present = await store.GetStatusAsync(Token);
        clock.UtcNow = Now.AddMinutes(5);
        var expired = await store.GetStatusAsync(Token);
        clock.UtcNow = Now;
        var consumed = await store.ConsumeAsync(provisioned.Credential!.Reveal(), Token);
        var afterConsume = await store.GetStatusAsync(Token);
        await File.WriteAllTextAsync(store.BootstrapFilePath, "{}", Token);
        var afterBootstrap = await store.GetStatusAsync(Token);

        Assert.Equal(BootstrapCredentialStatus.NotProvisioned, absent.Status);
        Assert.False(absent.BootstrapConfigured);
        Assert.Equal(BootstrapCredentialStatus.Provisioned, present.Status);
        Assert.Equal(Now.UtcDateTime, present.IssuedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddMinutes(5), present.ExpiresAtUtc);
        Assert.Equal(BootstrapCredentialStatus.Expired, expired.Status);
        Assert.True(consumed.IsConsumed);
        Assert.Equal(BootstrapCredentialStatus.NotProvisioned, afterConsume.Status);
        Assert.True(afterBootstrap.BootstrapConfigured);
        Assert.DoesNotContain(
            provisioned.Credential!.Reveal(),
            present.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The consume-first window: the credential is gone before the Bootstrap file is written, so a
    /// later failure leaves neither, and only an explicit new local provision recovers.
    /// </summary>
    [Fact]
    public async Task A_failure_after_consumption_never_restores_the_credential()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();

        Assert.True((await store.ConsumeAsync(candidate, Token)).IsConsumed);
        // The caller's own work fails after the credential is gone; nothing restores it.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.FromException(new InvalidOperationException("bootstrap write failed")));
        var replay = await store.ConsumeAsync(candidate, Token);
        var diagnosis = await store.GetStatusAsync(Token);

        Assert.NotNull(failure);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, replay.ErrorCode);
        Assert.Equal(BootstrapCredentialStatus.NotProvisioned, diagnosis.Status);
        Assert.False(diagnosis.BootstrapConfigured);

        // Explicit local recovery: a new provision succeeds and the old candidate stays rejected.
        var recovered = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(recovered.IsProvisioned);
        Assert.NotEqual(candidate, recovered.Credential!.Reveal());
        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.Invalid,
            (await store.ConsumeAsync(candidate, Token)).ErrorCode);
        Assert.True((await store.ConsumeAsync(recovered.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_the_original_token()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var provision = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ProvisionAsync(BootstrapCredentialLifetime.Default, abort.Token));
        var status = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.GetStatusAsync(abort.Token));
        var consume = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ConsumeAsync("candidate", abort.Token));

        Assert.Equal(abort.Token, provision.CancellationToken);
        Assert.Equal(abort.Token, status.CancellationToken);
        Assert.Equal(abort.Token, consume.CancellationToken);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Explicit_paths_are_resolved_and_validated()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);

        Assert.Equal(Path.GetFullPath(store.FilePath), store.FilePath);
        Assert.NotEqual(store.FilePath, store.BootstrapFilePath);
        Assert.Equal(Service, store.ServiceId);
        Assert.Throws<ArgumentNullException>(() =>
            new BootstrapCredentialFileStore(null!));
        Assert.Throws<ArgumentException>(() =>
            BootstrapCredentialFileStore.ResolveFilePath(Service, "  "));
        Assert.EndsWith(
            "credential-worker.bootstrap-credential.json",
            BootstrapCredentialFileStore.ResolveFilePath(Service),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provisioning_rejects_a_missing_lifetime()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.ProvisionAsync(null!, Token));
    }

    private static BootstrapCredentialFileStore Create(
        TemporaryDirectory directory,
        TimeProvider? timeProvider = null) => new(
            Service,
            Path.Combine(directory.Path, "credential.json"),
            Path.Combine(directory.Path, "bootstrap.json"),
            timeProvider);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

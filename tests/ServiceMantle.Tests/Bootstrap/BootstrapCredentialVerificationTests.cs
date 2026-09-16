using System.Text;
using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the non-consuming verification of a Bootstrap creation credential candidate: the record
/// stays byte-for-byte intact, the invalid classifications are indistinguishable, damaged or
/// denied storage fails closed as unavailable, cancellation wins at the boundaries, and the single
/// successful consumer is still decided by consumption alone.
/// </summary>
public sealed class BootstrapCredentialVerificationTests
{
    private static readonly ServiceId Service = ServiceId.Parse("credential-verifier");

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_candidate_verifies_without_touching_the_record_and_still_consumes_afterwards()
    {
        using var directory = TemporaryDirectory.Create();
        var clock = new MutableTimeProvider(Now);
        var store = Create(directory, clock);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();
        var bytesBefore = await File.ReadAllBytesAsync(store.FilePath, Token);
        var writeTimeBefore = File.GetLastWriteTimeUtc(store.FilePath);

        var first = await store.VerifyAsync(candidate, Token);
        var second = await store.VerifyAsync(candidate, Token);

        Assert.Equal(BootstrapCredentialVerificationState.Valid, first.State);
        Assert.Equal(BootstrapCredentialVerificationState.Valid, second.State);
        Assert.Null(first.ErrorCode);
        // The record is untouched: the format version, bytes, and modification time are unchanged.
        Assert.Equal(1, BootstrapCredentialFileStore.FormatVersion);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(store.FilePath, Token));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(store.FilePath));
        // Verification is not consumption: the credential still consumable exactly once.
        Assert.True((await store.ConsumeAsync(candidate, Token)).IsConsumed);
    }

    [Fact]
    public async Task Expired_mismatched_consumed_and_unprovisioned_candidates_are_indistinguishable()
    {
        using var directory = TemporaryDirectory.Create();
        var clock = new MutableTimeProvider(Now);
        var store = Create(directory, clock);
        var unprovisioned = await Create(directory).VerifyAsync(
            BootstrapCredential.Generate().Reveal(),
            Token);
        var provisioned = await store.ProvisionAsync(
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(1)),
            Token);
        var candidate = provisioned.Credential!.Reveal();
        clock.UtcNow = Now.AddMinutes(1);
        var expired = await store.VerifyAsync(candidate, Token);
        clock.UtcNow = Now;
        var mismatched = await store.VerifyAsync(BootstrapCredential.Generate().Reveal(), Token);
        Assert.True((await store.ConsumeAsync(candidate, Token)).IsConsumed);
        var consumed = await store.VerifyAsync(candidate, Token);

        var results = new[] { unprovisioned, expired, mismatched, consumed };
        Assert.All(results, result =>
        {
            Assert.Equal(BootstrapCredentialVerificationState.Invalid, result.State);
            Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, result.ErrorCode);
        });
        // The four outcomes carry no distinguishing information at all.
        Assert.Single(results.Select(result => result.ToString()).Distinct(StringComparer.Ordinal));
        Assert.Single(results.Select(result => result.ErrorCode).Distinct(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("short")]
    public async Task An_unparseable_candidate_is_invalid_without_writing_anything(string? candidate)
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var bytesBefore = await File.ReadAllBytesAsync(store.FilePath, Token);

        var rejected = await store.VerifyAsync(candidate, Token);

        Assert.Equal(BootstrapCredentialVerificationState.Invalid, rejected.State);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, rejected.ErrorCode);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(store.FilePath, Token));
        Assert.True((await store.ConsumeAsync(provisioned.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Theory]
    [InlineData("not-json", "not json at all")]
    [InlineData("nested", """{"formatVersion":{"a":{"b":{"c":{"d":1}}}}}""")]
    public async Task A_damaged_record_fails_closed_as_unavailable(string scenario, string content)
    {
        Assert.NotEqual("consumed", scenario);
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var candidate = BootstrapCredential.Generate().Reveal();
        await File.WriteAllTextAsync(store.FilePath, content, Token);
        var bytesBefore = await File.ReadAllBytesAsync(store.FilePath, Token);

        var result = await store.VerifyAsync(candidate, Token);

        Assert.Equal(BootstrapCredentialVerificationState.Unavailable, result.State);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, result.ErrorCode);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(store.FilePath, Token));
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
            $$"""{"formatVersion":1,"digest":"{{BootstrapCredentialDigest.Compute(credential).Value}}","issuedAtUtc":"2026-09-16T12:00:00.0000000Z","expiresAtUtc":"2026-09-16T12:15:00.0000000Z"}{{padding}}""",
            Token);

        var result = await store.VerifyAsync(credential.Reveal(), Token);

        Assert.Equal(BootstrapCredentialVerificationState.Unavailable, result.State);
        Assert.True(
            new FileInfo(store.FilePath).Length > BootstrapCredentialFileStore.MaximumFileByteCount);
    }

    [Fact]
    public async Task An_inaccessible_record_fails_closed_as_unavailable_without_leaking_the_cause()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var candidate = BootstrapCredential.Generate().Reveal();
        await File.WriteAllTextAsync(store.FilePath, "{}", Token);
        if (OperatingSystem.IsWindows())
        {
            // A real exclusive handle refuses the production read deterministically.
            using var exclusive = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            var refused = await store.VerifyAsync(candidate, Token);
            Assert.Equal(BootstrapCredentialVerificationState.Unavailable, refused.State);
            Assert.DoesNotContain(store.FilePath, refused.ToString(), StringComparison.Ordinal);
            return;
        }

        File.SetUnixFileMode(store.FilePath, UnixFileMode.None);
        BootstrapCredentialVerificationResult denied;
        try
        {
            Assert.SkipWhen(
                CanOpen(store.FilePath),
                "The current user bypasses the file mode, so no access failure can be observed.");
            denied = await store.VerifyAsync(candidate, Token);
        }
        finally
        {
            File.SetUnixFileMode(
                store.FilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(BootstrapCredentialVerificationState.Unavailable, denied.State);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, denied.ErrorCode);
        Assert.DoesNotContain(store.FilePath, denied.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_verification_still_leaves_the_credential_consumable_exactly_once()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();

        foreach (var _ in Enumerable.Range(0, 5))
        {
            Assert.Equal(BootstrapCredentialVerificationState.Valid, (await store.VerifyAsync(candidate, Token)).State);
        }

        Assert.True((await store.ConsumeAsync(candidate, Token)).IsConsumed);
        Assert.False((await store.ConsumeAsync(candidate, Token)).IsConsumed);
        Assert.Equal(
            BootstrapCredentialVerificationState.Invalid,
            (await store.VerifyAsync(candidate, Token)).State);
    }

    [Fact]
    public async Task A_candidate_differing_only_in_the_last_character_is_still_compared_in_full()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();
        // The stored comparison is the existing fixed-time digest match: the candidate below parses
        // and differs only in its final character, so a length- or prefix-shortcut comparison would
        // have to answer differently here.
        var lastCharacter = candidate[^1];
        var twisted = candidate[..^1] + (lastCharacter == 'A' ? 'B' : 'A');

        var result = await store.VerifyAsync(twisted, Token);

        Assert.Equal(BootstrapCredentialVerificationState.Invalid, result.State);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, result.ErrorCode);
    }

    [Fact]
    public async Task Continuous_verification_never_disturbs_exactly_one_successful_consumer()
    {
        const int Contenders = 16;
        using var directory = TemporaryDirectory.Create();
        var provisioned = await Create(directory).ProvisionAsync(
            BootstrapCredentialLifetime.Default,
            Token);
        var candidate = provisioned.Credential!.Reveal();
        using var stopVerifying = new CancellationTokenSource();
        var verifier = Task.Run(async () =>
        {
            var store = Create(directory);
            var observations = 0;
            while (!stopVerifying.IsCancellationRequested)
            {
                await store.VerifyAsync(candidate, Token).AsTask();
                observations++;
            }

            return observations;
        }, Token);
        try
        {
            var candidates = Enumerable.Range(0, Contenders)
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
                result => Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, result.ErrorCode));
        }
        finally
        {
            await stopVerifying.CancelAsync();
            await verifier.WaitAsync(Token);
        }

        // The record is gone and stays gone: verification never restored or preserved it.
        Assert.False(File.Exists(Create(directory).FilePath));
    }

    [Fact]
    public async Task An_already_cancelled_caller_never_reads_the_record()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var candidate = BootstrapCredential.Generate().Reveal();
        await File.WriteAllTextAsync(store.FilePath, "{}", Token);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        FileStream? refusal = null;
        if (OperatingSystem.IsWindows())
        {
            refusal = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            File.SetUnixFileMode(store.FilePath, UnixFileMode.None);
            Assert.SkipWhen(
                CanOpen(store.FilePath),
                "The current user bypasses the file mode, so a read refusal cannot be observed.");
        }

        try
        {
            // Reverse validation of the boundary: were the record read before the cancellation
            // checkpoint, the refused read would answer unavailable; the caller's cancellation
            // wins instead and the file is never opened.
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.VerifyAsync(candidate, abort.Token).AsTask());
            Assert.Equal(abort.Token, error.CancellationToken);
        }
        finally
        {
            refusal?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    store.FilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public async Task No_result_or_projection_ever_carries_the_candidate_digest_or_path()
    {
        using var directory = TemporaryDirectory.Create();
        using var emptyDirectory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(1)),
            Token);
        var candidate = provisioned.Credential!.Reveal();
        var digest = BootstrapCredentialDigest.Compute(provisioned.Credential!).Value;
        var otherCandidate = BootstrapCredential.Generate().Reveal();

        // A group of inputs: valid, mismatched, unparseable, unprovisioned, and damaged.
        var valid = await store.VerifyAsync(candidate, Token);
        var mismatched = await store.VerifyAsync(otherCandidate, Token);
        var malformed = await store.VerifyAsync("not-a-credential", Token);
        var unprovisioned = await Create(emptyDirectory).VerifyAsync(candidate, Token);
        var damagedStore = Create(directory);
        await File.WriteAllTextAsync(damagedStore.FilePath, "not json", Token);
        var damaged = await damagedStore.VerifyAsync(candidate, Token);
        var projections = new[] { valid, mismatched, malformed, unprovisioned, damaged };

        Assert.All(projections, result =>
        {
            Assert.DoesNotContain(candidate, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(digest, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(store.FilePath, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(otherCandidate, result.ToString(), StringComparison.Ordinal);
        });
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

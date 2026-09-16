using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the store-owned reissue of the one-time Bootstrap creation credential: only proven
/// Bootstrap absence authorizes it, a readable record is replaced atomically while an unreadable
/// one is never touched, the previous plaintext is invalidated immediately, and cancellation is
/// honored only before the replacement begins.
/// </summary>
public sealed class BootstrapCredentialReissueTests
{
    private const int MaximumFileByteCount = BootstrapCredentialFileStore.MaximumFileByteCount;

    private static readonly ServiceId Service = ServiceId.Parse("credential-reissue");

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_present_bootstrap_file_refuses_the_reissue_without_touching_the_record()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var provisioned = await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default, Token);
        await File.WriteAllTextAsync(paths.Bootstrap, "{}", Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var probeCount = 0;
        var store = Create(paths, new FixedClock(Now), path =>
        {
            probeCount++;
            return BootstrapFilePresenceProbe.Open(path);
        });

        var reissued = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(provisioned.IsProvisioned);
        Assert.False(reissued.IsProvisioned);
        Assert.Null(reissued.Credential);
        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.BootstrapConfigured,
            reissued.ErrorCode);
        Assert.Equal(1, probeCount);
        // The refusal neither read for replacement nor modified the record.
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("io")]
    public async Task An_unestablished_bootstrap_file_closes_the_reissue_without_touching_the_record(
        string failure)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        // A valid, unexpired record: a reissue that had classified existence and read it would
        // replace the record, so unchanged bytes plus the refusal prove the operation closed at
        // the existence evidence, before the record influenced anything.
        await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default, Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var probeCount = 0;
        var store = Create(paths, new FixedClock(Now), path =>
        {
            probeCount++;
            return failure == "denied"
                ? throw new UnauthorizedAccessException("denied")
                : throw new IOException("io");
        });

        var reissued = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.False(reissued.IsProvisioned);
        Assert.Null(reissued.Credential);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, reissued.ErrorCode);
        Assert.Equal(1, probeCount);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
        AssertReplacedNothing(paths);
    }

    [Fact]
    public async Task No_record_reissues_like_a_provision_and_supports_reissue_after_consumption()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var store = Create(paths, new FixedClock(Now));

        var first = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(first.IsProvisioned);
        Assert.True((await store.ConsumeAsync(first.Credential!.Reveal(), Token)).IsConsumed);

        // The consumed record is gone, so a reissue provisions a fresh credential again.
        var second = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(second.IsProvisioned);
        Assert.NotEqual(first.Credential!.Reveal(), second.Credential!.Reveal());
        Assert.True((await store.ConsumeAsync(second.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Fact]
    public async Task A_readable_unexpired_record_is_replaced_and_the_old_plaintext_is_invalid()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var store = Create(paths, new FixedClock(Now));
        var first = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.AlreadyExists,
            (await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token)).ErrorCode);

        var second = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(second.IsProvisioned);
        var oldPlaintext = first.Credential!.Reveal();
        Assert.NotEqual(oldPlaintext, second.Credential!.Reveal());
        Assert.Equal(
            BootstrapCredentialVerificationState.Invalid,
            (await store.VerifyAsync(oldPlaintext, Token)).State);
        Assert.Equal(
            WellKnownBootstrapCredentialErrorCodes.Invalid,
            (await store.ConsumeAsync(oldPlaintext, Token)).ErrorCode);
        AssertReplacedNothing(paths);
        Assert.True((await store.ConsumeAsync(second.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Fact]
    public async Task An_expired_record_is_replaced_with_a_consumable_credential()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var clock = new MutableClock(Now);
        var store = Create(paths, clock);
        await store.ProvisionAsync(
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(1)), Token);

        clock.UtcNow = Now.AddMinutes(5);
        Assert.Equal(BootstrapCredentialStatus.Expired, (await store.GetStatusAsync(Token)).Status);

        var reissued = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.True(reissued.IsProvisioned);
        Assert.Equal(
            BootstrapCredentialStatus.Provisioned,
            (await store.GetStatusAsync(Token)).Status);
        AssertReplacedNothing(paths);
        Assert.True((await store.ConsumeAsync(reissued.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("oversized")]
    [InlineData("unknown-member")]
    public async Task An_unparseable_record_is_refused_and_stays_byte_for_byte_unchanged(string shape)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default, Token);
        var valid = await File.ReadAllTextAsync(paths.Credential, Token);
        await File.WriteAllTextAsync(paths.Credential, UnparseableRecord(shape, valid), Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var store = Create(paths, new FixedClock(Now));

        var reissued = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.False(reissued.IsProvisioned);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, reissued.ErrorCode);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
        AssertReplacedNothing(paths);
    }

    [Theory]
    [InlineData("staging-write")]
    [InlineData("replacing-move")]
    [InlineData("seam-reported")]
    public async Task A_failed_replacement_keeps_the_old_record_and_leaves_no_staging_file(string kind)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var store = Create(paths, new FixedClock(Now));
        var first = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var failing = Create(paths, new FixedClock(Now), tryReplaceRecord: (stagingPath, payload, _) =>
        {
            if (kind == "replacing-move")
            {
                // The staging write succeeds, the overwriting move does not.
                File.WriteAllBytes(stagingPath, payload);
            }

            if (kind == "seam-reported")
            {
                return false;
            }

            throw new IOException("injected " + kind);
        });

        var reissued = await failing.ReissueAsync(BootstrapCredentialLifetime.Default, Token);

        Assert.False(reissued.IsProvisioned);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, reissued.ErrorCode);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
        // The old plaintext still owns the unchanged record.
        Assert.Equal(
            BootstrapCredentialVerificationState.Valid,
            (await store.VerifyAsync(first.Credential!.Reveal(), Token)).State);
        AssertReplacedNothing(paths);
    }

    [Fact]
    public async Task Repeated_reissues_stay_atomic_and_private_against_a_concurrent_reader()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var store = Create(paths, new FixedClock(Now));
        await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        // A concurrent reader keeps opening the record exactly the way the store does. Every
        // successful read must be a complete, in-protocol record: a torn write would surface as
        // bytes that are not a valid JSON document.
        var reads = 0;
        using var stop = new CancellationTokenSource();
        var firstRead = new TaskCompletionSource();
        var reader = Task.Run(
            () =>
            {
                while (!stop.Token.IsCancellationRequested)
                {
                    byte[]? content = null;
                    try
                    {
                        using var stream = BootstrapCredentialFileStore.OpenRecordForRead(paths.Credential);
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        content = memory.ToArray();
                    }
                    catch (IOException)
                    {
                        // A refused open or a momentarily absent record is share contention with
                        // the replacement, not a torn record.
                    }

                    if (content is not null)
                    {
                        Assert.InRange(content.Length, 1, MaximumFileByteCount);
                        using var _ = JsonDocument.Parse(content);
                        Interlocked.Increment(ref reads);
                        firstRead.TrySetResult();
                    }
                }
            },
            Token);

        // The replacements only start once the reader has actually observed the record, so the
        // concurrent-read evidence cannot be skipped by scheduling alone.
        await firstRead.Task.WaitAsync(Token);
        for (var i = 0; i < 100; i++)
        {
            Assert.True((await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token)).IsProvisioned);
        }

        stop.Cancel();
        await reader;
        Assert.True(Volatile.Read(ref reads) > 0, "the concurrent reader must have observed the record");
        AssertReplacedNothing(paths);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(paths.Credential));
        }
    }

    [Fact]
    public async Task Concurrent_reissues_from_no_record_produce_exactly_one_valid_plaintext()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var stores = Enumerable.Range(0, 16)
            .Select(_ => Create(paths, new FixedClock(Now)))
            .ToArray();
        using var start = new SemaphoreSlim(0);
        var races = stores.Select(store => Task.Run(async () =>
        {
            await start.WaitAsync(Token);
            return await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);
        })).ToArray();
        start.Release(stores.Length);
        var results = await Task.WhenAll(races);

        Assert.Contains(results, result => result.IsProvisioned);
        // Race losers report already_exists when their exclusive create lost, or unavailable when
        // their record read was refused by the winner's still-open exclusive write handle - the
        // read-failure classification of the contract, which share-enforcing runtimes (Windows,
        // .NET on macOS) produce during the write window. Neither ever carries a plaintext.
        Assert.All(results, result =>
            Assert.True(result.IsProvisioned ||
                result.ErrorCode is WellKnownBootstrapCredentialErrorCodes.AlreadyExists or
                    WellKnownBootstrapCredentialErrorCodes.Unavailable));

        // Every distinct plaintext the callers could have obtained, tested against the final
        // record: exactly one still verifies, and it comes from a provisioned result.
        var plaintexts = results
            .Where(result => result.IsProvisioned)
            .Select(result => result.Credential!.Reveal())
            .Distinct()
            .ToArray();
        var valid = new List<string>();
        foreach (var plaintext in plaintexts)
        {
            if ((await stores[0].VerifyAsync(plaintext, Token)).State ==
                BootstrapCredentialVerificationState.Valid)
            {
                valid.Add(plaintext);
            }
        }

        Assert.Single(valid);
        Assert.Contains(valid[0], plaintexts);
        AssertReplacedNothing(paths);
    }

    [Fact]
    public async Task Cancellation_before_entry_propagates_the_callers_token_without_any_work()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        using var abort = new CancellationTokenSource();
        await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default, Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var probeCount = 0;
        var store = Create(paths, new FixedClock(Now), path =>
        {
            probeCount++;
            return BootstrapFilePresenceProbe.Open(path);
        });
        abort.Cancel();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReissueAsync(BootstrapCredentialLifetime.Default, abort.Token));

        // The entry boundary fired before the existence probe ran at all.
        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Equal(0, probeCount);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
        AssertReplacedNothing(paths);
    }

    [Fact]
    public async Task Cancellation_after_the_existence_probe_propagates_the_callers_token()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        using var abort = new CancellationTokenSource();
        var probeCount = 0;
        var store = Create(paths, new FixedClock(Now), path =>
        {
            probeCount++;
            abort.Cancel();
            return BootstrapFilePresenceProbe.Open(path);
        });

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReissueAsync(BootstrapCredentialLifetime.Default, abort.Token));

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        // The reissue classified absence once and honored the cancellation at its own boundary; a
        // reissue that had fallen through to the provision path would have probed a second time.
        Assert.Equal(1, probeCount);
        Assert.False(File.Exists(paths.Credential));
        Assert.Empty(Directory.GetFiles(paths.Path));
    }

    [Fact]
    public async Task Cancellation_before_the_write_aborts_the_reissue_without_any_file_change()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        using var abort = new CancellationTokenSource();
        await Create(paths, new FixedClock(Now)).ProvisionAsync(
            BootstrapCredentialLifetime.Default, Token);
        var recordBefore = await File.ReadAllBytesAsync(paths.Credential, Token);
        var store = Create(
            paths,
            new CancellingClock(abort),
            tryReplaceRecord: (_, _, _) => throw new IOException("must not be reached"));

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReissueAsync(BootstrapCredentialLifetime.Default, abort.Token));

        // The token was cancelled while the new record's timestamps were read, before the staging
        // write: the pre-write boundary honors it and the record stays byte-for-byte unchanged.
        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Equal(recordBefore, await File.ReadAllBytesAsync(paths.Credential, Token));
        AssertReplacedNothing(paths);
    }

    [Fact]
    public async Task Cancellation_arriving_after_the_last_boundary_still_returns_the_plaintext()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        using var abort = new CancellationTokenSource();
        var store = Create(paths, new FixedClock(Now));
        var first = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var cancelling = Create(paths, new FixedClock(Now), tryReplaceRecord: (stagingPath, payload, filePath) =>
        {
            // The replacement has begun: a cancellation arriving here must not discard the only
            // copy of the new plaintext, so the real write-and-move runs after cancelling.
            abort.Cancel();
            File.WriteAllBytes(stagingPath, payload);
            File.Move(stagingPath, filePath, overwrite: true);
            return true;
        });

        var reissued = await cancelling.ReissueAsync(BootstrapCredentialLifetime.Default, abort.Token);

        Assert.True(reissued.IsProvisioned);
        Assert.Equal(
            BootstrapCredentialVerificationState.Invalid,
            (await store.VerifyAsync(first.Credential!.Reveal(), Token)).State);
        AssertReplacedNothing(paths);
        Assert.True((await store.ConsumeAsync(reissued.Credential!.Reveal(), Token)).IsConsumed);
    }

    [Fact]
    public async Task Neither_the_record_nor_any_projection_carries_the_plaintext()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = Paths.For(directory);
        var store = Create(paths, new FixedClock(Now));

        var reissued = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);
        var plaintext = reissued.Credential!.Reveal();

        // A second reissue replaces the record; neither record generation, nor any projection,
        // nor a cancellation carries the plaintext of the first issuance.
        var replacing = await store.ReissueAsync(BootstrapCredentialLifetime.Default, Token);
        Assert.True(replacing.IsProvisioned);
        var status = await store.GetStatusAsync(Token);

        Assert.DoesNotContain(plaintext, await File.ReadAllTextAsync(paths.Credential, Token));
        Assert.DoesNotContain(plaintext, reissued.ToString());
        Assert.DoesNotContain(plaintext, replacing.ToString());
        Assert.DoesNotContain(plaintext, status.ToString());

        using var abort = new CancellationTokenSource();
        abort.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReissueAsync(BootstrapCredentialLifetime.Default, abort.Token));
        Assert.DoesNotContain(plaintext, cancelled.ToString());
    }

    private static string UnparseableRecord(string shape, string valid)
    {
        return shape switch
        {
            "not-json" => "{not json",
            "oversized" => "{\"digest\":\"" + new string('a', MaximumFileByteCount) + "\"}",
            _ => "{\"unknown\":" + valid[1..],
        };
    }

    private static void AssertReplacedNothing(Paths paths)
    {
        // The directory holds exactly the record file the protocol owns - no staging leftovers.
        Assert.Equal(
            new[] { Path.GetFileName(paths.Credential) },
            Directory.GetFiles(paths.Path).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static BootstrapCredentialFileStore Create(
        Paths paths,
        TimeProvider timeProvider,
        Func<string, FileStream>? openBootstrapProbe = null,
        Func<string, byte[], string, bool>? tryReplaceRecord = null) => new(
            Service,
            paths.Credential,
            paths.Bootstrap,
            timeProvider,
            openBootstrapProbe ?? BootstrapFilePresenceProbe.Open,
            tryReplaceRecord);

    private sealed record Paths(string Path, string Credential, string Bootstrap)
    {
        internal static Paths For(TemporaryDirectory directory) => new(
            directory.Path,
            System.IO.Path.Combine(directory.Path, "credential.json"),
            System.IO.Path.Combine(directory.Path, "signacore.bootstrap.json"));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>
    /// Cancels the source the first time the clock is read, so a cancellation deterministically
    /// arrives between the record-parse boundary and the staging write.
    /// </summary>
    private sealed class CancellingClock(CancellationTokenSource source) : TimeProvider
    {
        private int read;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref read) == 1)
            {
                source.Cancel();
            }

            return Now;
        }
    }
}

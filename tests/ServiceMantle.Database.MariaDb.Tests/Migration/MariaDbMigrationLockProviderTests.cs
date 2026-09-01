using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.MariaDb.Tests.Migration;

public sealed class MariaDbMigrationLockProviderTests
{
    [Theory]
    [InlineData("catalog", "sm:migration:652f55016243bf1b9f1bbea46d5749ef892dbe394e46de9d66a")]
    [InlineData("test-service", "sm:migration:665653223b1e8bfa2d462b3adb06d49f8984052e5df03d7fd23")]
    [InlineData("service-a", "sm:migration:cb4738f92915b3c7a05f248889f60ea465a27ce3c079f6ff98d")]
    public void Lock_name_matches_the_fixed_SHA256_vector(string serviceId, string expected)
    {
        var actual = MariaDbMigrationLockName.Derive(ServiceId.Parse(serviceId));

        Assert.Equal(expected, actual);
        Assert.Equal(64, actual.Length);
        Assert.Equal(actual.ToLowerInvariant(), actual);
    }

    [Fact]
    public void Lock_name_uses_the_normalized_case_insensitive_service_identifier()
    {
        Assert.Equal(
            MariaDbMigrationLockName.Derive(ServiceId.Parse("catalog")),
            MariaDbMigrationLockName.Derive(ServiceId.Parse("  Catalog  ")));
    }

    [Fact]
    public async Task Acquire_uses_an_isolated_target_session_and_remaining_bounded_timeout()
    {
        var operations = new FakeOperations();
        var provider = new MariaDbMigrationLockProvider(operations);

        await using var lease = await provider.AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(pooling: true, autoEnlist: true),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseProviderIds.MariaDb, provider.ProviderId);
        Assert.Equal(WellKnownDatabaseProviderIds.MariaDb, lease.ProviderId);
        Assert.NotNull(operations.ConnectionString);
        Assert.False(operations.ConnectionString.Pooling);
        Assert.False(operations.ConnectionString.AutoEnlist);
        Assert.Equal("app", operations.ExpectedDatabaseName);
        Assert.Equal(
            "sm:migration:652f55016243bf1b9f1bbea46d5749ef892dbe394e46de9d66a",
            operations.Session.LockName);
        Assert.InRange(operations.Session.TimeoutSeconds, 0.001, 5);
        Assert.InRange(operations.Session.CommandTimeoutSeconds, 1, 6);
    }

    public static TheoryData<int?, string> NonSuccessResults => new()
    {
        { 0, WellKnownMigrationErrorCodes.LockTimeout },
        { null, WellKnownMigrationErrorCodes.LockFailed },
        { 2, WellKnownMigrationErrorCodes.LockFailed },
        { -1, WellKnownMigrationErrorCodes.LockFailed },
    };

    [Theory]
    [MemberData(nameof(NonSuccessResults))]
    public async Task Acquire_results_map_to_the_fixed_contract(int? result, string expectedErrorCode)
    {
        var operations = new FakeOperations();
        operations.Session.AcquireResult = result;

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new MariaDbMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal(1, operations.Session.DisposeCount);
        Assert.Equal(0, operations.Session.ReleaseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connection_and_command_errors_are_safe_lock_failures(bool failDuringOpen)
    {
        const string secret = "provider-error-secret";
        var operations = new FakeOperations();
        if (failDuringOpen)
        {
            operations.OpenFailure = new InvalidOperationException(secret);
        }
        else
        {
            operations.Session.AcquireFailure = new InvalidOperationException(secret);
        }

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new MariaDbMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(failDuringOpen ? 0 : 1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Overall_timeout_cancels_an_in_flight_command_and_closes_the_session()
    {
        var operations = new FakeOperations();
        operations.Session.BlockAcquireUntilCancellation = true;
        var acquisition = new MariaDbMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken).AsTask();
        await operations.Session.AcquireStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(() => acquisition);

        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, exception.ErrorCode);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Caller_cancellation_precedes_timeout_and_raw_provider_failure()
    {
        const string secret = "caller-cancel-secret";
        var operations = new FakeOperations();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        operations.Session.AcquireHandler = _ =>
        {
            cancellation.Cancel();
            throw new InvalidOperationException($"Server=internal;Password={secret}");
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new MariaDbMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromMilliseconds(100),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Explicit_disposal_releases_and_closes_once_without_signalling_loss()
    {
        var operations = new FakeOperations();
        var lease = await new MariaDbMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var leaseLost = lease.LeaseLost;

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, operations.Session.ReleaseCount);
        Assert.Equal(1, operations.Session.DisposeCount);
        Assert.False(leaseLost.IsCancellationRequested);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Release_outcome_or_cleanup_failure_does_not_escape(
        int? releaseResult,
        bool throwOnRelease)
    {
        var operations = new FakeOperations();
        operations.Session.ReleaseResult = releaseResult;
        operations.Session.ReleaseFailure = throwOnRelease
            ? new InvalidOperationException("release-secret")
            : null;
        var lease = await new MariaDbMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await lease.DisposeAsync();

        Assert.Equal(1, operations.Session.ReleaseCount);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Probe_failure_signals_permanent_lease_loss_and_cleanup_still_releases_once()
    {
        var operations = new FakeOperations();
        operations.Session.ProbeFailure = new InvalidOperationException("probe-secret");
        var lease = await new MariaDbMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var leaseLost = lease.LeaseLost;
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = leaseLost.Register(() => signalled.TrySetResult());

        await signalled.Task.WaitAsync(
            MariaDbMigrationLock.LeaseLossDetectionBound,
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        Assert.True(leaseLost.IsCancellationRequested);
        Assert.Equal(1, operations.Session.ProbeCount);
        Assert.Equal(1, operations.Session.ReleaseCount);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Theory]
    [InlineData("MySQL", "11.4", "Server=internal;Database=app;User ID=user;Password=secret")]
    [InlineData("MariaDB", "10.10", "Server=internal;Database=app;User ID=user;Password=secret")]
    [InlineData("MariaDB", "11.4-MariaDB", "Server=internal;Database=app;User ID=user;Password=secret")]
    [InlineData("MariaDB", "11.4", "Server=internal;User ID=user;Password=secret")]
    public async Task Invalid_target_fails_without_opening_or_exposing_target_values(
        string provider,
        string version,
        string connectionString)
    {
        var operations = new FakeOperations();
        var bootstrap = new BootstrapDatabaseConfiguration(provider, version, connectionString);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new MariaDbMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                bootstrap,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.Null(operations.ConnectionString);
        Assert.DoesNotContain("internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_range_and_pre_cancelled_token_are_validated_deterministically()
    {
        var provider = new MariaDbMigrationLockProvider(new FakeOperations());
        foreach (var timeout in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromMilliseconds(-1),
                     TimeSpan.FromMilliseconds(uint.MaxValue)
                 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await provider.AcquireAsync(
                    ServiceId.Parse("catalog"),
                    CreateBootstrap(),
                    timeout,
                    TestContext.Current.CancellationToken));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                cancellation.Token));
    }

    private static BootstrapDatabaseConfiguration CreateBootstrap(
        bool pooling = false,
        bool autoEnlist = false)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "mariadb.internal",
            Database = "app",
            UserID = "target-user",
            Password = "target-password",
            Pooling = pooling,
            AutoEnlist = autoEnlist
        };
        return new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MariaDb,
            "11.4.8",
            builder.ConnectionString);
    }

    private sealed class FakeOperations : IMariaDbMigrationLockOperations
    {
        internal FakeSession Session { get; } = new();

        internal Exception? OpenFailure { get; set; }

        internal MySqlConnectionStringBuilder? ConnectionString { get; private set; }

        internal string? ExpectedDatabaseName { get; private set; }

        public ValueTask<IMariaDbMigrationLockSession> OpenSessionAsync(
            MySqlConnectionStringBuilder connectionString,
            string expectedDatabaseName,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionString = new MySqlConnectionStringBuilder(connectionString.ConnectionString);
            ExpectedDatabaseName = expectedDatabaseName;
            return OpenFailure is null
                ? ValueTask.FromResult<IMariaDbMigrationLockSession>(Session)
                : ValueTask.FromException<IMariaDbMigrationLockSession>(OpenFailure);
        }
    }

    private sealed class FakeSession : IMariaDbMigrationLockSession
    {
        internal TaskCompletionSource AcquireStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long ConnectionId => 42;

        internal string? LockName { get; private set; }

        internal double TimeoutSeconds { get; private set; }

        internal int CommandTimeoutSeconds { get; private set; }

        internal int? AcquireResult { get; set; } = 1;

        internal Exception? AcquireFailure { get; set; }

        internal Func<CancellationToken, int?>? AcquireHandler { get; set; }

        internal bool BlockAcquireUntilCancellation { get; set; }

        internal Exception? ProbeFailure { get; set; }

        internal int? ReleaseResult { get; set; } = 1;

        internal Exception? ReleaseFailure { get; set; }

        internal int ProbeCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public async ValueTask<int?> AcquireLockAsync(
            string lockName,
            double timeoutSeconds,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            LockName = lockName;
            TimeoutSeconds = timeoutSeconds;
            CommandTimeoutSeconds = commandTimeoutSeconds;
            AcquireStarted.TrySetResult();
            if (AcquireFailure is not null)
            {
                throw AcquireFailure;
            }

            if (BlockAcquireUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return AcquireHandler?.Invoke(cancellationToken) ?? AcquireResult;
        }

        public ValueTask ProbeLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return ProbeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(ProbeFailure);
        }

        public ValueTask<int?> ReleaseLockAsync(
            string lockName,
            CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return ReleaseFailure is null
                ? ValueTask.FromResult(ReleaseResult)
                : ValueTask.FromException<int?>(ReleaseFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

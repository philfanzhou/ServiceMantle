using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests.Migration;

public sealed class OracleMigrationLockProviderTests
{
    [Theory]
    [InlineData(
        "catalog",
        "ServiceMantle.Migration.652f55016243bf1b9f1bbea46d5749ef892dbe394e46de9d66ab1aacf0b4af57")]
    [InlineData(
        "test-service",
        "ServiceMantle.Migration.665653223b1e8bfa2d462b3adb06d49f8984052e5df03d7fd2365293a102fce8")]
    [InlineData(
        "service-a",
        "ServiceMantle.Migration.cb4738f92915b3c7a05f248889f60ea465a27ce3c079f6ff98dd6b5519a8d221")]
    public void Lock_name_matches_the_fixed_SHA256_vector(string serviceId, string expected) =>
        Assert.Equal(expected, OracleMigrationLockName.Derive(ServiceId.Parse(serviceId)));

    [Fact]
    public void Lock_name_uses_the_normalized_service_identifier()
    {
        var normalized = OracleMigrationLockName.Derive(ServiceId.Parse("catalog"));
        var candidate = OracleMigrationLockName.Derive(ServiceId.Parse("  Catalog  "));

        Assert.Equal(normalized, candidate);
        Assert.Equal(88, candidate.Length);
    }

    [Fact]
    public async Task Acquire_uses_an_isolated_target_session_and_remaining_bounded_timeout()
    {
        var operations = new FakeOperations();
        var provider = new OracleMigrationLockProvider(operations);

        await using var lease = await provider.AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(pooling: true, enlist: true),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseProviderIds.Oracle, provider.ProviderId);
        Assert.Equal(WellKnownDatabaseProviderIds.Oracle, lease.ProviderId);
        Assert.NotNull(operations.ConnectionString);
        Assert.False(operations.ConnectionString.Pooling);
        Assert.Equal("false", operations.ConnectionString["Enlist"]?.ToString());
        Assert.Equal("TARGET_USER", operations.ExpectedUserName);
        Assert.Equal(
            "ServiceMantle.Migration.652f55016243bf1b9f1bbea46d5749ef892dbe394e46de9d66ab1aacf0b4af57",
            operations.Session.LockName);
        Assert.InRange(operations.Session.RequestTimeoutSeconds, 1, 5);
    }

    [Theory]
    [InlineData(1, WellKnownMigrationErrorCodes.LockTimeout)]
    [InlineData(2, WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData(3, WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData(4, WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData(5, WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData(6, WellKnownMigrationErrorCodes.LockFailed)]
    public async Task Request_return_codes_map_to_the_contract(int resultCode, string expectedErrorCode)
    {
        var operations = new FakeOperations();
        operations.Session.RequestResult = resultCode;
        var provider = new OracleMigrationLockProvider(operations);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await provider.AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_direct_DBMS_LOCK_permission_is_not_supported(bool failDuringAllocation)
    {
        var operations = new FakeOperations();
        if (failDuringAllocation)
        {
            operations.Session.AllocateFailure = new OracleMigrationLockOperationException(
                OracleMigrationLockFailureKind.NotSupported);
        }
        else
        {
            operations.Session.RequestFailure = new OracleMigrationLockOperationException(
                OracleMigrationLockFailureKind.NotSupported);
        }

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new OracleMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, exception.ErrorCode);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Allocator_connection_and_runtime_failures_are_lock_failed(int stage)
    {
        var operations = new FakeOperations();
        var failure = new OracleMigrationLockOperationException(OracleMigrationLockFailureKind.Failed);
        switch (stage)
        {
            case 0:
                operations.OpenFailure = failure;
                break;
            case 1:
                operations.Session.AllocateFailure = failure;
                break;
            default:
                operations.Session.RequestFailure = failure;
                break;
        }

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new OracleMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.Equal(stage == 0 ? 0 : 1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Overall_timeout_cancels_an_in_flight_request_and_cleans_up()
    {
        var operations = new FakeOperations();
        operations.Session.BlockRequestUntilCancellation = true;
        var acquisition = new OracleMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken).AsTask();
        await operations.Session.RequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(() => acquisition);

        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, exception.ErrorCode);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Caller_cancellation_precedes_timeout_and_cleans_up()
    {
        var operations = new FakeOperations();
        operations.Session.BlockRequestUntilCancellation = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var acquisition = new OracleMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            cancellation.Token).AsTask();
        await operations.Session.RequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Explicit_disposal_releases_once_closes_once_and_does_not_signal_loss()
    {
        var operations = new FakeOperations();
        var lease = await new OracleMigrationLockProvider(operations).AcquireAsync(
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

    [Fact]
    public async Task Probe_failure_signals_permanent_lease_loss_and_cleanup_still_runs()
    {
        var operations = new FakeOperations();
        operations.Session.ProbeFailure = new OracleMigrationLockOperationException(
            OracleMigrationLockFailureKind.Failed);
        await using var lease = await new OracleMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease.LeaseLost.Register(() => signalled.TrySetResult());

        await signalled.Task.WaitAsync(
            OracleMigrationLock.LeaseLossDetectionBound,
            TestContext.Current.CancellationToken);

        Assert.True(lease.LeaseLost.IsCancellationRequested);
        Assert.Equal(1, operations.Session.ProbeCount);
    }

    [Fact]
    public async Task Invalid_target_fails_without_opening_a_session_or_exposing_secrets()
    {
        const string secret = "Super-Secret-Target-Password";
        var bootstrap = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.Oracle,
            "18.0",
            $"User Id=TARGET_USER;Password={secret};Data Source=oracle.internal/FREEPDB1");
        var operations = new FakeOperations();

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new OracleMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                bootstrap,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.Null(operations.ConnectionString);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("oracle.internal", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_runtime_topology_fails_closed()
    {
        var operations = new FakeOperations
        {
            OpenFailure = new OracleMigrationLockOperationException(
                OracleMigrationLockFailureKind.Failed)
        };

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new OracleMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.Null(operations.Session.LockName);
    }

    [Fact]
    public async Task Cancellation_before_open_does_not_create_a_session()
    {
        var operations = new FakeOperations();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new OracleMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                cancellation.Token));

        Assert.Null(operations.ConnectionString);
    }

    private static BootstrapDatabaseConfiguration CreateBootstrap(
        bool pooling = false,
        bool enlist = false)
    {
        var builder = new OracleConnectionStringBuilder
        {
            UserID = "TARGET_USER",
            Password = "Target-Password-1",
            DataSource = "oracle.internal/FREEPDB1",
            Pooling = pooling,
            Enlist = enlist ? "true" : "false"
        };
        return new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.Oracle,
            "23.26.1.0",
            builder.ConnectionString);
    }

    private sealed class FakeOperations : IOracleMigrationLockOperations
    {
        internal FakeSession Session { get; } = new();

        internal Exception? OpenFailure { get; set; }

        internal OracleConnectionStringBuilder? ConnectionString { get; private set; }

        internal string? ExpectedUserName { get; private set; }

        public ValueTask<IOracleMigrationLockSession> OpenSessionAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionString = new OracleConnectionStringBuilder(connectionString.ConnectionString);
            ExpectedUserName = expectedUserName;
            return OpenFailure is null
                ? ValueTask.FromResult<IOracleMigrationLockSession>(Session)
                : ValueTask.FromException<IOracleMigrationLockSession>(OpenFailure);
        }
    }

    private sealed class FakeSession : IOracleMigrationLockSession
    {
        internal TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long SessionId => 42;

        internal string? LockName { get; private set; }

        internal int RequestTimeoutSeconds { get; private set; }

        internal int RequestResult { get; set; }

        internal Exception? AllocateFailure { get; set; }

        internal Exception? RequestFailure { get; set; }

        internal Exception? ProbeFailure { get; set; }

        internal bool BlockRequestUntilCancellation { get; set; }

        internal int ProbeCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<string> AllocateLockHandleAsync(
            string lockName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LockName = lockName;
            return AllocateFailure is null
                ? ValueTask.FromResult("test-lock-handle")
                : ValueTask.FromException<string>(AllocateFailure);
        }

        public async ValueTask<int> RequestLockAsync(
            string lockHandle,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-lock-handle", lockHandle);
            RequestTimeoutSeconds = timeoutSeconds;
            RequestStarted.TrySetResult();
            if (RequestFailure is not null)
            {
                throw RequestFailure;
            }

            if (BlockRequestUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return RequestResult;
        }

        public ValueTask ProbeLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return ProbeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(ProbeFailure);
        }

        public ValueTask<int> ReleaseLockAsync(
            string lockHandle,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-lock-handle", lockHandle);
            ReleaseCount++;
            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests.Migration;

public sealed class SqlServerMigrationLockProviderTests
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
    public void Resource_name_matches_the_fixed_SHA256_vector(string serviceId, string expected)
    {
        var actual = SqlServerMigrationLockName.Derive(ServiceId.Parse(serviceId));

        Assert.Equal(expected, actual);
        Assert.Equal(88, actual.Length);
        Assert.Equal(expected["ServiceMantle.Migration.".Length..].ToLowerInvariant(),
            actual["ServiceMantle.Migration.".Length..]);
    }

    [Fact]
    public void Resource_name_uses_the_normalized_service_identifier() =>
        Assert.Equal(
            SqlServerMigrationLockName.Derive(ServiceId.Parse("catalog")),
            SqlServerMigrationLockName.Derive(ServiceId.Parse("  Catalog  ")));

    [Fact]
    public async Task Acquire_uses_an_isolated_target_session_and_remaining_millisecond_timeout()
    {
        var operations = new FakeOperations();
        var provider = new SqlServerMigrationLockProvider(operations);

        await using var lease = await provider.AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(pooling: true, enlist: true, connectRetryCount: 4),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, provider.ProviderId);
        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, lease.ProviderId);
        Assert.NotNull(operations.ConnectionString);
        Assert.False(operations.ConnectionString.Pooling);
        Assert.False(operations.ConnectionString.Enlist);
        Assert.Equal(0, operations.ConnectionString.ConnectRetryCount);
        Assert.Equal("app", operations.ExpectedDatabaseName);
        Assert.Equal(
            "ServiceMantle.Migration.652f55016243bf1b9f1bbea46d5749ef892dbe394e46de9d66ab1aacf0b4af57",
            operations.Session.ResourceName);
        Assert.InRange(operations.Session.TimeoutMilliseconds, 1, 5000);
        Assert.InRange(operations.Session.CommandTimeoutSeconds, 1, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Official_success_codes_return_a_lease(int resultCode)
    {
        var operations = new FakeOperations();
        operations.Session.AcquireResult = resultCode;

        await using var lease = await new SqlServerMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, lease.ProviderId);
    }

    public static TheoryData<int, string> NonSuccessCodes => new()
    {
        { -1, WellKnownMigrationErrorCodes.LockTimeout },
        { -2, WellKnownMigrationErrorCodes.LockFailed },
        { -3, WellKnownMigrationErrorCodes.LockFailed },
        { -999, WellKnownMigrationErrorCodes.LockFailed },
        { -4, WellKnownMigrationErrorCodes.LockFailed },
        { 2, WellKnownMigrationErrorCodes.LockFailed },
    };

    [Theory]
    [MemberData(nameof(NonSuccessCodes))]
    public async Task Non_success_codes_map_to_the_fixed_contract(
        int resultCode,
        string expectedErrorCode)
    {
        var operations = new FakeOperations();
        operations.Session.AcquireResult = resultCode;

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new SqlServerMigrationLockProvider(operations).AcquireAsync(
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
    public async Task Application_lock_permission_denial_is_not_supported(bool failDuringOpen)
    {
        var operations = new FakeOperations();
        var failure = new SqlServerMigrationLockOperationException(
            SqlServerMigrationLockFailureKind.NotSupported);
        if (failDuringOpen)
        {
            operations.OpenFailure = failure;
        }
        else
        {
            operations.Session.AcquireFailure = failure;
        }

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new SqlServerMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, exception.ErrorCode);
        Assert.Equal(failDuringOpen ? 0 : 1, operations.Session.DisposeCount);
    }

    [Theory]
    [InlineData(229, true)]
    [InlineData(15151, true)]
    [InlineData(916, false)]
    [InlineData(-2, false)]
    public void Permission_classifier_is_restricted_to_application_lock_access(
        int errorNumber,
        bool expected) =>
        Assert.Equal(
            expected,
            SqlServerMigrationLockSession.IsApplicationLockPermissionDenied(errorNumber));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connection_and_command_errors_are_safe_lock_failures(bool failDuringOpen)
    {
        const string secret = "provider-error-secret";
        var operations = new FakeOperations();
        var failure = new InvalidOperationException(
            $"Server=sql.internal;Database=app;User ID=admin;Password={secret};EXEC sp_getapplock");
        if (failDuringOpen)
        {
            operations.OpenFailure = failure;
        }
        else
        {
            operations.Session.AcquireFailure = failure;
        }

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new SqlServerMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sql.internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sp_getapplock", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overall_timeout_cancels_an_in_flight_command_and_closes_the_session()
    {
        var operations = new FakeOperations();
        operations.Session.BlockAcquireUntilCancellation = true;
        var acquisition = new SqlServerMigrationLockProvider(operations).AcquireAsync(
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
    public async Task Caller_cancellation_precedes_return_code_timeout_and_raw_failure()
    {
        const string secret = "caller-cancel-secret";
        var operations = new FakeOperations();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        operations.Session.AcquireHandler = _ =>
        {
            cancellation.Cancel();
            throw new InvalidOperationException($"Password={secret};Resource=private-resource");
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new SqlServerMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                CreateBootstrap(),
                TimeSpan.FromMilliseconds(100),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-resource", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Explicit_disposal_releases_and_closes_once_without_signalling_loss()
    {
        var operations = new FakeOperations();
        var lease = await new SqlServerMigrationLockProvider(operations).AcquireAsync(
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
    [InlineData(0, false)]
    [InlineData(-999, false)]
    [InlineData(0, true)]
    public async Task Release_outcome_or_cleanup_failure_does_not_escape(
        int releaseResult,
        bool throwOnRelease)
    {
        var operations = new FakeOperations();
        operations.Session.ReleaseResult = releaseResult;
        operations.Session.ReleaseFailure = throwOnRelease
            ? new InvalidOperationException("release-secret")
            : null;
        var lease = await new SqlServerMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await lease.DisposeAsync();

        Assert.Equal(1, operations.Session.ReleaseCount);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Fact]
    public async Task Probe_failure_signals_permanent_lease_loss_and_cleanup_still_runs()
    {
        var operations = new FakeOperations();
        operations.Session.ProbeFailure = new InvalidOperationException("probe-secret");
        var lease = await new SqlServerMigrationLockProvider(operations).AcquireAsync(
            ServiceId.Parse("catalog"),
            CreateBootstrap(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var leaseLost = lease.LeaseLost;
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = leaseLost.Register(() => signalled.TrySetResult());

        await signalled.Task.WaitAsync(
            SqlServerMigrationLock.LeaseLossDetectionBound,
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        Assert.True(leaseLost.IsCancellationRequested);
        Assert.Equal(1, operations.Session.ProbeCount);
        Assert.Equal(1, operations.Session.ReleaseCount);
        Assert.Equal(1, operations.Session.DisposeCount);
    }

    [Theory]
    [InlineData("PostgreSQL", "16", "Server=sql.internal;Initial Catalog=app;User ID=user;Password=secret")]
    [InlineData("SqlServer", "14", "Server=sql.internal;Initial Catalog=app;User ID=user;Password=secret")]
    [InlineData("SqlServer", "16-CU", "Server=sql.internal;Initial Catalog=app;User ID=user;Password=secret")]
    [InlineData("SqlServer", "16", "Server=sql.internal;User ID=user;Password=secret")]
    public async Task Invalid_target_fails_without_opening_or_exposing_target_values(
        string provider,
        string version,
        string connectionString)
    {
        var operations = new FakeOperations();
        var bootstrap = new BootstrapDatabaseConfiguration(provider, version, connectionString);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new SqlServerMigrationLockProvider(operations).AcquireAsync(
                ServiceId.Parse("catalog"),
                bootstrap,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        Assert.Null(operations.ConnectionString);
        Assert.DoesNotContain("sql.internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("app", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_range_and_pre_cancelled_token_are_validated_deterministically()
    {
        var provider = new SqlServerMigrationLockProvider(new FakeOperations());
        foreach (var timeout in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromMilliseconds(-1),
                     TimeSpan.FromMilliseconds((double)int.MaxValue + 1D)
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
        bool enlist = false,
        int connectRetryCount = 0)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "sql.internal",
            InitialCatalog = "app",
            UserID = "target-user",
            Password = "target-password",
            TrustServerCertificate = true,
            Pooling = pooling,
            Enlist = enlist,
            ConnectRetryCount = connectRetryCount
        };
        return new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.SqlServer,
            "16.0.1000.6",
            builder.ConnectionString);
    }

    private sealed class FakeOperations : ISqlServerMigrationLockOperations
    {
        internal FakeSession Session { get; } = new();

        internal Exception? OpenFailure { get; set; }

        internal SqlConnectionStringBuilder? ConnectionString { get; private set; }

        internal string? ExpectedDatabaseName { get; private set; }

        public ValueTask<ISqlServerMigrationLockSession> OpenSessionAsync(
            SqlConnectionStringBuilder connectionString,
            string expectedDatabaseName,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionString = new SqlConnectionStringBuilder(connectionString.ConnectionString);
            ExpectedDatabaseName = expectedDatabaseName;
            return OpenFailure is null
                ? ValueTask.FromResult<ISqlServerMigrationLockSession>(Session)
                : ValueTask.FromException<ISqlServerMigrationLockSession>(OpenFailure);
        }
    }

    private sealed class FakeSession : ISqlServerMigrationLockSession
    {
        internal TaskCompletionSource AcquireStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SessionId => 42;

        internal string? ResourceName { get; private set; }

        internal int TimeoutMilliseconds { get; private set; }

        internal int CommandTimeoutSeconds { get; private set; }

        internal int AcquireResult { get; set; }

        internal int ReleaseResult { get; set; }

        internal Exception? AcquireFailure { get; set; }

        internal Exception? ProbeFailure { get; set; }

        internal Exception? ReleaseFailure { get; set; }

        internal Func<CancellationToken, int>? AcquireHandler { get; set; }

        internal bool BlockAcquireUntilCancellation { get; set; }

        internal int ProbeCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public async ValueTask<int> AcquireLockAsync(
            string resourceName,
            int timeoutMilliseconds,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            ResourceName = resourceName;
            TimeoutMilliseconds = timeoutMilliseconds;
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

        public ValueTask ProbeLeaseAsync(
            string resourceName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ResourceName, resourceName);
            ProbeCount++;
            return ProbeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(ProbeFailure);
        }

        public ValueTask<int> ReleaseLockAsync(
            string resourceName,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ResourceName, resourceName);
            ReleaseCount++;
            return ReleaseFailure is null
                ? ValueTask.FromResult(ReleaseResult)
                : ValueTask.FromException<int>(ReleaseFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

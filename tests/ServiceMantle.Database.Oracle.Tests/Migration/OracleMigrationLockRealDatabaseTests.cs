using System.Data;
using System.Diagnostics;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle.Migration;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests.Migration;

/// <summary>Exercises Oracle <c>SYS.DBMS_LOCK</c> against the pinned FREEPDB1 environment.</summary>
[RealDatabaseTest(RealDatabaseProvider.Oracle)]
public sealed class OracleMigrationLockRealDatabaseTests
{
    private const string AdminConnectionVariable = "SERVICEMANTLE_ORACLE_ADMIN_CONNECTION_STRING";
    private const string TargetPassword = "Lock-Target-Real-1";

    [Fact]
    public async Task Same_service_is_exclusive_different_services_are_independent_and_release_reacquires()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_LOCK");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: true);
        try
        {
            var bootstrap = CreateBootstrap(admin, user);
            var provider = new OracleMigrationLockProvider();
            var serviceA = ServiceId.Parse($"oracle-a-{Guid.NewGuid():N}");
            var serviceB = ServiceId.Parse($"oracle-b-{Guid.NewGuid():N}");
            var leaseA = await provider.AcquireAsync(
                serviceA,
                bootstrap,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            var sessionId = Assert.IsType<OracleMigrationLock>(leaseA).SessionId;
            try
            {
                var timeout = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                    await provider.AcquireAsync(
                        serviceA,
                        bootstrap,
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken));
                Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, timeout.ErrorCode);

                await using var leaseB = await provider.AcquireAsync(
                    serviceB,
                    bootstrap,
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);
                Assert.NotNull(leaseB);
            }
            finally
            {
                await leaseA.DisposeAsync();
            }

            await WaitForSessionToCloseAsync(admin, user, sessionId);
            await using var reacquired = await provider.AcquireAsync(
                serviceA,
                bootstrap,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(reacquired.LeaseLost.IsCancellationRequested);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Caller_cancellation_while_waiting_for_REQUEST_is_preserved()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_CANCEL");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: true);
        try
        {
            var bootstrap = CreateBootstrap(admin, user);
            var serviceId = ServiceId.Parse($"oracle-cancel-{Guid.NewGuid():N}");
            var holderProvider = new OracleMigrationLockProvider();
            await using var holder = await holderProvider.AcquireAsync(
                serviceId,
                bootstrap,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            var requestStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var contenderProvider = new OracleMigrationLockProvider(
                new RequestSignallingOperations(new OracleMigrationLockOperations(), requestStarted));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var contender = contenderProvider.AcquireAsync(
                serviceId,
                bootstrap,
                TimeSpan.FromSeconds(30),
                cancellation.Token).AsTask();
            await requestStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await contender.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Missing_direct_execute_grant_is_lock_not_supported_and_safe()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_NOGRANT");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: false);
        try
        {
            var bootstrap = CreateBootstrap(admin, user);
            var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                await new OracleMigrationLockProvider().AcquireAsync(
                    ServiceId.Parse($"oracle-no-grant-{Guid.NewGuid():N}"),
                    bootstrap,
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));

            Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, exception.ErrorCode);
            Assert.DoesNotContain(TargetPassword, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(admin, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Session_terminated_after_open_but_before_allocation_fails_acquisition_closed()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_PREKILL");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: true);
        var sessionOpened = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAcquisition = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var provider = new OracleMigrationLockProvider(
                new OpenSessionGateOperations(
                    new OracleMigrationLockOperations(),
                    sessionOpened,
                    continueAcquisition));
            var acquisition = provider.AcquireAsync(
                ServiceId.Parse($"oracle-pre-kill-{Guid.NewGuid():N}"),
                CreateBootstrap(admin, user),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken).AsTask();
            var sessionId = await sessionOpened.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            await KillSessionAsync(admin, user, sessionId);
            continueAcquisition.TrySetResult();
            var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(() => acquisition);

            Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
        }
        finally
        {
            continueAcquisition.TrySetResult();
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Theory]
    [InlineData((int)OrchestrationStage.InitialInspection)]
    [InlineData((int)OrchestrationStage.Execution)]
    [InlineData((int)OrchestrationStage.FinalInspection)]
    public async Task Holding_session_termination_during_each_stage_fails_closed(int stageValue)
    {
        var stage = (OrchestrationStage)stageValue;
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_STAGE");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: true);
        try
        {
            var stageReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new StageGatedExecutor(stage, stageReached);
            var lockProvider = new CapturingMigrationLockProvider();
            var registry = new DatabaseMigrationLockProviderRegistry(
                [lockProvider],
                DatabaseProviderIdResolver.Empty);
            var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);
            var orchestration = orchestrator.OrchestrateMigrationAsync(
                ServiceId.Parse($"oracle-stage-{stageValue}-{Guid.NewGuid():N}"),
                CreateBootstrap(admin, user),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken).AsTask();
            await stageReached.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            var lease = Assert.IsType<OracleMigrationLock>(lockProvider.AcquiredLease);

            await KillSessionAsync(admin, user, lease.SessionId);
            var detection = Stopwatch.StartNew();
            var result = await orchestration.WaitAsync(
                OracleMigrationLock.LeaseLossDetectionBound,
                TestContext.Current.CancellationToken);
            detection.Stop();

            Assert.True(detection.Elapsed <= OracleMigrationLock.LeaseLossDetectionBound);
            Assert.False(result.Succeeded);
            Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
            Assert.Equal(stage != OrchestrationStage.InitialInspection, result.ExecutorWasCalled);
            Assert.Equal(1, executor.InitialInspectionCount);
            Assert.Equal(stage == OrchestrationStage.InitialInspection ? 0 : 1, executor.ExecutionCount);
            Assert.Equal(stage == OrchestrationStage.FinalInspection ? 1 : 0, executor.FinalInspectionCount);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Two_real_orchestrators_execute_once_and_the_contender_rechecks_under_the_lock()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_ORCH");
        var table = NewIdentifier("SM_MIG_STATE");
        await CreateTargetUserAsync(admin, user, grantDbmsLock: true);
        await CreateMigrationStateAsync(admin, user, table);
        try
        {
            var bootstrap = CreateBootstrap(admin, user);
            var targetConnection = new OracleConnectionStringBuilder(bootstrap.ConnectionString)
            {
                Pooling = false,
                Enlist = "false"
            }.ConnectionString;
            var serviceId = ServiceId.Parse($"oracle-orchestrator-{Guid.NewGuid():N}");
            var executionStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExecution = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRequestStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstProvider = new OracleMigrationLockProvider();
            var secondProvider = new OracleMigrationLockProvider(
                new RequestSignallingOperations(
                    new OracleMigrationLockOperations(),
                    secondRequestStarted));
            var firstExecutor = new RealStateExecutor(
                targetConnection,
                table,
                executionStarted,
                allowExecution);
            var secondExecutor = new RealStateExecutor(
                targetConnection,
                table,
                executionStarted,
                allowExecution);
            var first = CreateOrchestrator(firstProvider, firstExecutor).OrchestrateMigrationAsync(
                serviceId,
                bootstrap,
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken).AsTask();
            await executionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            var held = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                await new OracleMigrationLockProvider().AcquireAsync(
                    serviceId,
                    bootstrap,
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));
            Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, held.ErrorCode);

            var second = CreateOrchestrator(secondProvider, secondExecutor).OrchestrateMigrationAsync(
                serviceId,
                bootstrap,
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken).AsTask();
            await secondRequestStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            allowExecution.TrySetResult();

            await Task.WhenAll(first, second).WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var firstResult = await first;
            var secondResult = await second;

            Assert.True(firstResult.Succeeded);
            Assert.True(secondResult.Succeeded);
            Assert.True(firstResult.ExecutorWasCalled);
            Assert.False(secondResult.ExecutorWasCalled);
            Assert.Equal(1, await ReadExecutionCountAsync(admin, table));
            Assert.Equal(2, firstExecutor.InspectionCount);
            Assert.Equal(1, secondExecutor.InspectionCount);
        }
        finally
        {
            await DropTableIfPresentAsync(admin, table);
            await DropUserIfPresentAsync(admin, user);
        }
    }

    private static DatabaseMigrationOrchestrator CreateOrchestrator(
        IDatabaseMigrationLockProvider provider,
        IDatabaseMigrationExecutor executor) =>
        new(
            executor,
            new DatabaseMigrationLockProviderRegistry(
                [provider],
                DatabaseProviderIdResolver.Empty));

    private static string RequireAdminConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(AdminConnectionVariable);
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.Oracle,
            !string.IsNullOrWhiteSpace(value));
        return new OracleConnectionStringBuilder(value!)
        {
            Pooling = false,
            Enlist = "false"
        }.ConnectionString;
    }

    private static BootstrapDatabaseConfiguration CreateBootstrap(string admin, string user)
    {
        var builder = new OracleConnectionStringBuilder(admin)
        {
            UserID = user,
            Password = TargetPassword,
            Pooling = true,
            Enlist = "true"
        };
        return new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.Oracle,
            "23.26.1.0",
            builder.ConnectionString);
    }

    private static string NewIdentifier(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 30)].ToUpperInvariant();

    private static async Task CreateTargetUserAsync(
        string admin,
        string user,
        bool grantDbmsLock)
    {
        await DropUserIfPresentAsync(admin, user);
        await ExecuteAdminAsync(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{TargetPassword}\"");
        await ExecuteAdminAsync(admin, $"GRANT CREATE SESSION TO \"{user}\"");
        if (grantDbmsLock)
        {
            await ExecuteAdminAsync(
                CreateSysDbaConnectionString(admin),
                $"GRANT EXECUTE ON SYS.DBMS_LOCK TO \"{user}\"");
        }
    }

    private static string CreateSysDbaConnectionString(string admin)
    {
        var builder = new OracleConnectionStringBuilder(admin)
        {
            UserID = "sys",
            Pooling = false,
            Enlist = "false"
        };
        builder["DBA Privilege"] = "SYSDBA";
        return builder.ConnectionString;
    }

    private static async Task CreateMigrationStateAsync(string admin, string user, string table)
    {
        await DropTableIfPresentAsync(admin, table);
        await ExecuteAdminAsync(
            admin,
            $"CREATE TABLE \"{table}\" (STATE VARCHAR2(20) NOT NULL, EXECUTION_COUNT NUMBER(10) NOT NULL)");
        await ExecuteAdminAsync(
            admin,
            $"INSERT INTO \"{table}\" (STATE, EXECUTION_COUNT) VALUES ('pending', 0)");
        await ExecuteAdminAsync(admin, "COMMIT");
        await ExecuteAdminAsync(admin, $"GRANT SELECT, UPDATE ON \"{table}\" TO \"{user}\"");
    }

    private static async Task<int> ReadExecutionCountAsync(string admin, string table)
    {
        var result = await ExecuteAdminScalarAsync(
            admin,
            $"SELECT EXECUTION_COUNT FROM \"{table}\"");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task KillSessionAsync(string admin, string user, long sessionId)
    {
        await using var connection = new OracleConnection(admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.BindByName = true;
        lookup.CommandText =
            "SELECT SERIAL# FROM V$SESSION WHERE SID = :session_id " +
            "AND USERNAME = :user_name AND ROWNUM = 1";
        lookup.Parameters.Add(
            "session_id",
            OracleDbType.Decimal,
            sessionId,
            ParameterDirection.Input);
        lookup.Parameters.Add(
            "user_name",
            OracleDbType.Varchar2,
            user,
            ParameterDirection.Input);
        var serialValue = await lookup.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(serialValue);
        var serial = Convert.ToInt64(serialValue, CultureInfo.InvariantCulture);
        await using var kill = connection.CreateCommand();
        kill.CommandText = $"ALTER SYSTEM KILL SESSION '{sessionId},{serial}' IMMEDIATE";
        try
        {
            await kill.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (OracleException exception) when (exception.Number == 31)
        {
            // ORA-00031 confirms that Oracle marked the exact session for termination.
        }
    }

    private static async Task WaitForSessionToCloseAsync(string admin, string user, long sessionId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var connection = new OracleConnection(admin);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText =
                "SELECT COUNT(*) FROM V$SESSION WHERE SID = :session_id AND USERNAME = :user_name";
            command.Parameters.Add(
                "session_id",
                OracleDbType.Decimal,
                sessionId,
                ParameterDirection.Input);
            command.Parameters.Add(
                "user_name",
                OracleDbType.Varchar2,
                user,
                ParameterDirection.Input);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                    CultureInfo.InvariantCulture) == 0)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The unpooled Oracle migration lock session remained visible after disposal.");
    }

    private static async Task DropUserIfPresentAsync(string admin, string user)
    {
        if (Convert.ToInt32(
                await ExecuteAdminScalarAsync(
                    admin,
                    $"SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = '{user}'"),
                CultureInfo.InvariantCulture) != 0)
        {
            await ExecuteAdminAsync(admin, $"DROP USER \"{user}\" CASCADE");
        }
    }

    private static async Task DropTableIfPresentAsync(string admin, string table)
    {
        if (Convert.ToInt32(
                await ExecuteAdminScalarAsync(
                    admin,
                    $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '{table}'"),
                CultureInfo.InvariantCulture) != 0)
        {
            await ExecuteAdminAsync(admin, $"DROP TABLE \"{table}\" PURGE");
        }
    }

    private static async Task<object?> ExecuteAdminScalarAsync(string admin, string sql)
    {
        await using var connection = new OracleConnection(admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAdminAsync(string admin, string sql)
    {
        await using var connection = new OracleConnection(admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private enum OrchestrationStage
    {
        InitialInspection,
        Execution,
        FinalInspection
    }

    private sealed class CapturingMigrationLockProvider : IDatabaseMigrationLockProvider
    {
        private readonly OracleMigrationLockProvider inner = new();

        public string ProviderId => inner.ProviderId;

        internal IDatabaseMigrationLock? AcquiredLease { get; private set; }

        public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            AcquiredLease = await inner.AcquireAsync(
                serviceId,
                bootstrap,
                acquireTimeout,
                cancellationToken);
            return AcquiredLease;
        }
    }

    private sealed class StageGatedExecutor(
        OrchestrationStage stage,
        TaskCompletionSource stageReached) : IDatabaseMigrationExecutor
    {
        internal int InitialInspectionCount { get; private set; }

        internal int ExecutionCount { get; private set; }

        internal int FinalInspectionCount { get; private set; }

        public async ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (InitialInspectionCount == 0)
            {
                InitialInspectionCount++;
                if (stage == OrchestrationStage.InitialInspection)
                {
                    stageReached.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return MigrationObservationState.PendingMigration;
            }

            FinalInspectionCount++;
            if (stage == OrchestrationStage.FinalInspection)
            {
                stageReached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return MigrationObservationState.CurrentVersionCompatible;
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            if (stage == OrchestrationStage.Execution)
            {
                stageReached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    private sealed class RealStateExecutor(
        string connectionString,
        string table,
        TaskCompletionSource executionStarted,
        TaskCompletionSource allowExecution) : IDatabaseMigrationExecutor
    {
        internal int InspectionCount { get; private set; }

        public async ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default)
        {
            InspectionCount++;
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT STATE FROM SYSTEM.\"{table}\"";
            var state = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
            return string.Equals(state, "current", StringComparison.Ordinal)
                ? MigrationObservationState.CurrentVersionCompatible
                : MigrationObservationState.PendingMigration;
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            executionStarted.TrySetResult();
            await allowExecution.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE SYSTEM.\"{table}\" SET STATE = 'current', " +
                "EXECUTION_COUNT = EXECUTION_COUNT + 1";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class OpenSessionGateOperations(
        IOracleMigrationLockOperations inner,
        TaskCompletionSource<long> sessionOpened,
        TaskCompletionSource continueAcquisition) : IOracleMigrationLockOperations
    {
        public async ValueTask<IOracleMigrationLockSession> OpenSessionAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken)
        {
            var session = await inner.OpenSessionAsync(
                connectionString,
                expectedUserName,
                cancellationToken);
            try
            {
                sessionOpened.TrySetResult(session.SessionId);
                await continueAcquisition.Task.WaitAsync(cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class RequestSignallingOperations(
        IOracleMigrationLockOperations inner,
        TaskCompletionSource requestStarted) : IOracleMigrationLockOperations
    {
        public async ValueTask<IOracleMigrationLockSession> OpenSessionAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken) =>
            new RequestSignallingSession(
                await inner.OpenSessionAsync(
                    connectionString,
                    expectedUserName,
                    cancellationToken),
                requestStarted);
    }

    private sealed class RequestSignallingSession(
        IOracleMigrationLockSession inner,
        TaskCompletionSource requestStarted) : IOracleMigrationLockSession
    {
        public long SessionId => inner.SessionId;

        public ValueTask<string> AllocateLockHandleAsync(
            string lockName,
            CancellationToken cancellationToken) =>
            inner.AllocateLockHandleAsync(lockName, cancellationToken);

        public ValueTask<int> RequestLockAsync(
            string lockHandle,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            requestStarted.TrySetResult();
            return inner.RequestLockAsync(lockHandle, timeoutSeconds, cancellationToken);
        }

        public ValueTask ProbeLeaseAsync(CancellationToken cancellationToken) =>
            inner.ProbeLeaseAsync(cancellationToken);

        public ValueTask<int> ReleaseLockAsync(
            string lockHandle,
            CancellationToken cancellationToken) =>
            inner.ReleaseLockAsync(lockHandle, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

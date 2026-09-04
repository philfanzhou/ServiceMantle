using System.Diagnostics;
using System.Globalization;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql.Migration;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Testcontainers.MySql;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests.Migration;

[RealDatabaseTest(RealDatabaseProvider.MySql)]
public sealed class MySqlMigrationLockRealDatabaseTests(
    MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>
{
    [Fact]
    public async Task Named_locks_contend_only_for_the_same_service_and_release_for_reacquisition()
    {
        var bootstrap = RequireBootstrap();
        var provider = new MySqlMigrationLockProvider();
        var serviceA = ServiceId.Parse($"mysql-a-{Guid.NewGuid():N}");
        var serviceB = ServiceId.Parse($"mysql-b-{Guid.NewGuid():N}");
        var leaseA = await provider.AcquireAsync(
            serviceA,
            bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        try
        {
            var timeout = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                await provider.AcquireAsync(
                    serviceA,
                    bootstrap,
                    TimeSpan.FromMilliseconds(250),
                    TestContext.Current.CancellationToken));
            Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, timeout.ErrorCode);
            Assert.DoesNotContain("test-password", timeout.ToString(), StringComparison.Ordinal);

            await using var leaseB = await provider.AcquireAsync(
                serviceB,
                bootstrap,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.False(leaseB.LeaseLost.IsCancellationRequested);
        }
        finally
        {
            await leaseA.DisposeAsync();
        }

        await leaseA.DisposeAsync();
        await using var reacquired = await provider.AcquireAsync(
            serviceA,
            bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(reacquired.LeaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_while_waiting_is_preserved_and_the_session_is_cleaned_up()
    {
        var bootstrap = RequireBootstrap();
        var serviceId = ServiceId.Parse($"mysql-cancel-{Guid.NewGuid():N}");
        var provider = new MySqlMigrationLockProvider();
        await using var holder = await provider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var contender = provider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(30),
            cancellation.Token).AsTask();
        await Task.Delay(250, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contender);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain("test-password", exception.ToString(), StringComparison.Ordinal);

        await holder.DisposeAsync();
        await using var reacquired = await provider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(reacquired.LeaseLost.IsCancellationRequested);
    }

    [Theory]
    [InlineData((int)OrchestrationStage.InitialInspection)]
    [InlineData((int)OrchestrationStage.Execution)]
    [InlineData((int)OrchestrationStage.FinalInspection)]
    public async Task Holding_connection_termination_fails_closed_before_the_next_stage(int stageValue)
    {
        var bootstrap = RequireBootstrap();
        var stage = (OrchestrationStage)stageValue;
        var stageReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new StageGatedExecutor(stage, stageReached);
        var lockProvider = new CapturingMigrationLockProvider();
        var orchestration = CreateOrchestrator(lockProvider, executor)
            .OrchestrateMigrationAsync(
                ServiceId.Parse($"mysql-stage-{stageValue}-{Guid.NewGuid():N}"),
                bootstrap,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken)
            .AsTask();
        await stageReached.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        var lease = Assert.IsType<MySqlMigrationLock>(lockProvider.AcquiredLease);

        await KillConnectionAsync(lease.ConnectionId);
        var detection = Stopwatch.StartNew();
        var result = await orchestration.WaitAsync(
            MySqlMigrationLock.LeaseLossDetectionBound,
            TestContext.Current.CancellationToken);
        detection.Stop();

        Assert.True(detection.Elapsed <= MySqlMigrationLock.LeaseLossDetectionBound);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.Equal(stage != OrchestrationStage.InitialInspection, result.ExecutorWasCalled);
        Assert.Equal(1, executor.InitialInspectionCount);
        Assert.Equal(stage == OrchestrationStage.InitialInspection ? 0 : 1, executor.ExecutionCount);
        Assert.Equal(stage == OrchestrationStage.FinalInspection ? 1 : 0, executor.FinalInspectionCount);
    }

    [Fact]
    public async Task Two_real_orchestrators_execute_once_and_the_contender_rechecks_under_the_lock()
    {
        var bootstrap = RequireBootstrap();
        var table = $"sm_migration_{Guid.NewGuid():N}";
        await CreateMigrationStateAsync(table);
        try
        {
            var serviceId = ServiceId.Parse($"mysql-orchestrator-{Guid.NewGuid():N}");
            var executionStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExecution = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAcquireStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstExecutor = new RealStateExecutor(
                RequireConnectionString(), table, executionStarted, allowExecution);
            var secondExecutor = new RealStateExecutor(
                RequireConnectionString(), table, executionStarted, allowExecution);
            var firstProvider = new MySqlMigrationLockProvider();
            var secondProvider = new MySqlMigrationLockProvider(
                new AcquireSignallingOperations(
                    new MySqlMigrationLockOperations(),
                    secondAcquireStarted));
            var first = CreateOrchestrator(firstProvider, firstExecutor)
                .OrchestrateMigrationAsync(
                    serviceId,
                    bootstrap,
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken)
                .AsTask();
            await executionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            var held = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                await new MySqlMigrationLockProvider().AcquireAsync(
                    serviceId,
                    bootstrap,
                    TimeSpan.FromMilliseconds(250),
                    TestContext.Current.CancellationToken));
            Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, held.ErrorCode);

            var second = CreateOrchestrator(secondProvider, secondExecutor)
                .OrchestrateMigrationAsync(
                    serviceId,
                    bootstrap,
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken)
                .AsTask();
            await secondAcquireStarted.Task.WaitAsync(
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
            Assert.Equal(1, await ReadExecutionCountAsync(table));
            Assert.Equal(2, firstExecutor.InspectionCount);
            Assert.Equal(1, secondExecutor.InspectionCount);
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS `{table}`");
        }
    }

    private BootstrapDatabaseConfiguration RequireBootstrap() => new(
        WellKnownDatabaseProviderIds.MySql,
        "8.4",
        RequireConnectionString());

    private string RequireConnectionString()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.MySql,
            !string.IsNullOrWhiteSpace(fixture.ConnectionString));
        return fixture.ConnectionString!;
    }

    private async Task KillConnectionAsync(long connectionId)
    {
        await using var connection = new MySqlConnection(RequireConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"KILL CONNECTION {connectionId.ToString(CultureInfo.InvariantCulture)}";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task CreateMigrationStateAsync(string table)
    {
        await ExecuteAsync(
            $"CREATE TABLE `{table}` (state VARCHAR(20) NOT NULL, execution_count INT NOT NULL)");
        await ExecuteAsync(
            $"INSERT INTO `{table}` (state, execution_count) VALUES ('pending', 0)");
    }

    private async Task<int> ReadExecutionCountAsync(string table)
    {
        await using var connection = new MySqlConnection(RequireConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT execution_count FROM `{table}`";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(RequireConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DatabaseMigrationOrchestrator CreateOrchestrator(
        IDatabaseMigrationLockProvider provider,
        IDatabaseMigrationExecutor executor) =>
        new(
            executor,
            new DatabaseMigrationLockProviderRegistry(
                [provider],
                DatabaseProviderIdResolver.Empty));

    private enum OrchestrationStage
    {
        InitialInspection,
        Execution,
        FinalInspection
    }

    private sealed class CapturingMigrationLockProvider : IDatabaseMigrationLockProvider
    {
        private readonly MySqlMigrationLockProvider inner = new();

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
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT state FROM `{table}`";
            var state = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
            return string.Equals(state, "current", StringComparison.Ordinal)
                ? MigrationObservationState.CurrentVersionCompatible
                : MigrationObservationState.PendingMigration;
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            executionStarted.TrySetResult();
            await allowExecution.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE `{table}` SET state = 'current', execution_count = execution_count + 1";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class AcquireSignallingOperations(
        IMySqlMigrationLockOperations inner,
        TaskCompletionSource acquireStarted) : IMySqlMigrationLockOperations
    {
        public async ValueTask<IMySqlMigrationLockSession> OpenSessionAsync(
            MySqlConnectionStringBuilder connectionString,
            string expectedDatabaseName,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken) =>
            new AcquireSignallingSession(
                await inner.OpenSessionAsync(
                    connectionString,
                    expectedDatabaseName,
                    commandTimeoutSeconds,
                    cancellationToken),
                acquireStarted);
    }

    private sealed class AcquireSignallingSession(
        IMySqlMigrationLockSession inner,
        TaskCompletionSource acquireStarted) : IMySqlMigrationLockSession
    {
        public long ConnectionId => inner.ConnectionId;

        public ValueTask<int?> AcquireLockAsync(
            string lockName,
            double timeoutSeconds,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            acquireStarted.TrySetResult();
            return inner.AcquireLockAsync(
                lockName,
                timeoutSeconds,
                commandTimeoutSeconds,
                cancellationToken);
        }

        public ValueTask ProbeLeaseAsync(CancellationToken cancellationToken) =>
            inner.ProbeLeaseAsync(cancellationToken);

        public ValueTask<int?> ReleaseLockAsync(
            string lockName,
            CancellationToken cancellationToken) =>
            inner.ReleaseLockAsync(lockName, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

public sealed class MySqlContainerFixture : IAsyncLifetime
{
    private const string DefaultImage = "mysql:8.4";
    private const string ImageVariable = "SERVICEMANTLE_MYSQL_IMAGE";
    private MySqlContainer? container;

    public string? ConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.MySql))
        {
            return;
        }

        var image = Environment.GetEnvironmentVariable(ImageVariable);
        container = new MySqlBuilder(
                string.IsNullOrWhiteSpace(image) ? DefaultImage : image)
            .WithDatabase("servicemantle")
            .WithUsername("servicemantle")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        ConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

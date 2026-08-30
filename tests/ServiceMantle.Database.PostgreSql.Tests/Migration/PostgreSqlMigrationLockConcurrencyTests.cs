using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Migration;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests.Migration;

/// <summary>
/// Real PostgreSQL advisory lock tests using Testcontainers.
/// These tests verify multi-instance locking behavior.
/// Enabled via RUN_SERVICEMANTLE_POSTGRES_TESTS=true and docker availability.
/// </summary>
public class PostgreSqlMigrationLockConcurrencyTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        var image = GetPostgresImage();
        container = new PostgreSqlBuilder(image)
            .WithPassword("test-password")
            .WithUsername("test-user")
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        connectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lock_SameServiceId_UseSameLockKey()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests(),
            "PostgreSQL tests disabled. Set RUN_SERVICEMANTLE_POSTGRES_TESTS=true to enable.");

        var serviceId = ServiceId.Parse("test-service");

        var key1 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);
        var key2 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task Lock_DifferentServiceIds_UseDifferentKeys()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests(),
            "PostgreSQL tests disabled. Set RUN_SERVICEMANTLE_POSTGRES_TESTS=true to enable.");

        var serviceId1 = ServiceId.Parse("service-1");
        var serviceId2 = ServiceId.Parse("service-2");

        var key1 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId1);
        var key2 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task Lock_SecondInstance_WaitsForFirstLockRelease()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();
        var lockTimeout = TimeSpan.FromSeconds(10);

        var acquireOrder = new List<int>();
        var lockEvent1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockEvent2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First instance acquires the lock and holds it for a bounded duration.
        var task1 = Task.Run(
            async () =>
            {
                var lock1 = await lockProvider.AcquireAsync(serviceId, bootstrap, lockTimeout, testToken);

                acquireOrder.Add(1);
                lockEvent1.SetResult(true);

                await Task.Delay(500, testToken);
                await lock1.DisposeAsync();
            },
            testToken);

        // Once lockEvent1 fires, the real PostgreSQL advisory lock is already held by task1,
        // so no additional padding delay is needed before starting the second attempt.
        await lockEvent1.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

        // Second instance tries to acquire the same lock and must wait for the first to release it.
        var task2 = Task.Run(
            async () =>
            {
                var lock2 = await lockProvider.AcquireAsync(serviceId, bootstrap, lockTimeout, testToken);

                acquireOrder.Add(2);
                lockEvent2.SetResult(true);

                await lock2.DisposeAsync();
            },
            testToken);

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(15), testToken);
        await lockEvent2.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

        // First instance should acquire before second
        Assert.Equal([1, 2], acquireOrder);
    }

    [Fact]
    public async Task Lock_Timeout_ReturnsErrorAfterTimeout()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();

        // Hold the first lock
        var lock1 = await lockProvider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(30),
            testToken);

        try
        {
            // Try to acquire with a short timeout while first lock is held
            var ex = await Assert.ThrowsAsync<DatabaseMigrationLockException>(
                async () =>
                {
                    await lockProvider.AcquireAsync(
                        serviceId,
                        bootstrap,
                        TimeSpan.FromMilliseconds(500),
                        testToken);
                });

            Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, ex.ErrorCode);
        }
        finally
        {
            await lock1.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lock_Cancellation_ThrowsOperationCanceledException()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();

        // Hold the first lock
        var lock1 = await lockProvider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(30),
            testToken);

        try
        {
            // Try to acquire with cancellation while first lock is held. This CTS deliberately
            // triggers cancellation independently of the test's own cancellation token, because
            // the cancellation itself is the behavior under test.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));

            // Cancellation may surface as the base type or as TaskCanceledException, depending on
            // whether it is observed at an explicit check or propagated from an awaited Task.Delay.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () =>
                {
                    await lockProvider.AcquireAsync(
                        serviceId,
                        bootstrap,
                        TimeSpan.FromSeconds(30),
                        cts.Token);
                });
        }
        finally
        {
            await lock1.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lock_ReleaseAndReacquire_SecondInstanceSucceeds()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();
        var lockTimeout = TimeSpan.FromSeconds(10);

        // First instance acquires and releases
        var lock1 = await lockProvider.AcquireAsync(
            serviceId,
            bootstrap,
            lockTimeout,
            testToken);

        await lock1.DisposeAsync();

        // Second instance should acquire successfully
        var lock2 = await lockProvider.AcquireAsync(
            serviceId,
            bootstrap,
            lockTimeout,
            testToken);

        Assert.NotNull(lock2);
        await lock2.DisposeAsync();
    }

    [Fact]
    public async Task Lock_NoSecrets_InExceptionMessages()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();

        // Hold the first lock
        var lock1 = await lockProvider.AcquireAsync(
            serviceId,
            bootstrap,
            TimeSpan.FromSeconds(30),
            testToken);

        try
        {
            var ex = await Assert.ThrowsAsync<DatabaseMigrationLockException>(
                async () =>
                {
                    await lockProvider.AcquireAsync(
                        serviceId,
                        bootstrap,
                        TimeSpan.FromMilliseconds(500),
                        testToken);
                });

            // Message and exception string should not contain connection string or password
            Assert.DoesNotContain("localhost", ex.Message);
            Assert.DoesNotContain("password", ex.Message);
            Assert.DoesNotContain("test-password", ex.Message);
            Assert.DoesNotContain("ConnectionString", ex.Message);

            var exString = ex.ToString();
            Assert.DoesNotContain("localhost", exString);
            Assert.DoesNotContain("password", exString);
            Assert.DoesNotContain("test-password", exString);
        }
        finally
        {
            await lock1.DisposeAsync();
        }
    }

    /// <summary>
    /// Deterministic proof that two different ServiceIds do not contend for the same advisory
    /// lock. service-a's lease is acquired and held open for the entire test body. If service-b
    /// incorrectly mapped to the same lock key, its acquisition (bounded to a short timeout)
    /// would throw <see cref="DatabaseMigrationLockException"/> with <c>LockTimeout</c>,
    /// failing the test. There is no reliance on wall-clock thresholds or background tasks.
    /// </summary>
    [Fact]
    public async Task Lock_DifferentServiceIds_DontCompete()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceIdA = ServiceId.Parse("service-a");
        var serviceIdB = ServiceId.Parse("service-b");

        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        var lockProvider = new PostgreSqlMigrationLockProvider();

        // Acquire and continuously hold service-a's lease for the duration of this test.
        var leaseA = await lockProvider.AcquireAsync(
            serviceIdA,
            bootstrap,
            TimeSpan.FromSeconds(30),
            testToken);

        try
        {
            // While A is still held, acquire B with a short, bounded timeout. If A and B were
            // incorrectly mapped to the same advisory lock key, this call would time out and
            // throw, failing the test deterministically instead of passing by coincidence.
            var leaseB = await lockProvider.AcquireAsync(
                serviceIdB,
                bootstrap,
                TimeSpan.FromSeconds(1),
                testToken);

            try
            {
                Assert.NotNull(leaseB);
            }
            finally
            {
                await leaseB.DisposeAsync();
            }
        }
        finally
        {
            await leaseA.DisposeAsync();
        }
    }

    /// <summary>
    /// Deterministic end-to-end proof that two concurrent orchestrator instances against the
    /// same ServiceId and the same real PostgreSQL database execute the migration exactly once.
    /// A shared gate holds the first executor inside ExecuteAsync (with the advisory lock still
    /// held) until the second orchestrator has begun its own acquisition attempt. If the
    /// advisory lock failed to provide mutual exclusion, both executors would reach ExecuteAsync
    /// and, once the gate is released, both would increment execution_count concurrently,
    /// making the final count 2 and failing the test.
    /// </summary>
    [Fact]
    public async Task OrchestratorDoubleInstance_OnlyOneExecutes_ViaAdvisoryLock()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var testToken = TestContext.Current.CancellationToken;
        var serviceId = ServiceId.Parse("test-service");
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            connectionString);

        await InitializeTestDatabase(testToken);

        try
        {
            var lockProvider = new PostgreSqlMigrationLockProvider();
            var lockTimeout = TimeSpan.FromSeconds(15);

            var executionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExecutionToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Both executors share the same gate. Only the instance holding the real advisory
            // lock is expected to ever reach ExecuteAsync; if the lock is broken and both do,
            // releasing the shared gate lets both proceed concurrently and the execution_count
            // assertion below catches it.
            var executor1 = new GatedRealDatabaseExecutor(connectionString!, executionStarted, allowExecutionToComplete);
            var executor2 = new GatedRealDatabaseExecutor(connectionString!, executionStarted, allowExecutionToComplete);

            var registry1 = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
            var registry2 = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

            var orchestrator1 = new DatabaseMigrationOrchestrator(executor1, registry1);
            var orchestrator2 = new DatabaseMigrationOrchestrator(executor2, registry2);

            var task1 = orchestrator1.OrchestrateMigrationAsync(
                serviceId,
                bootstrap,
                lockTimeout,
                testToken).AsTask();

            // Wait until some executor has entered ExecuteAsync (lock acquired, inspection done).
            await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

            // Confirm the advisory lock is genuinely still held at this point: a bounded probe
            // acquisition for the same ServiceId must time out, since the gated executor cannot
            // have released it yet (it is blocked awaiting allowExecutionToComplete).
            var lockStillHeld = await Assert.ThrowsAsync<DatabaseMigrationLockException>(
                async () =>
                {
                    await using var probe = await lockProvider.AcquireAsync(
                        serviceId,
                        bootstrap,
                        TimeSpan.FromMilliseconds(500),
                        testToken);
                });
            Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, lockStillHeld.ErrorCode);

            // Start the second orchestrator while the first is gated inside ExecuteAsync. The
            // lock cannot be released until we open the gate below, so this attempt is
            // guaranteed to begin (and, if it reaches its own polling loop, contend) while the
            // lock is still held by the first instance.
            var task2 = orchestrator2.OrchestrateMigrationAsync(
                serviceId,
                bootstrap,
                lockTimeout,
                testToken).AsTask();

            // Release the gate now that the second orchestrator's acquisition attempt is underway.
            allowExecutionToComplete.SetResult(true);

            await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(20), testToken);

            var result1 = await task1;
            var result2 = await task2;

            // Both should succeed
            Assert.True(result1.Succeeded);
            Assert.True(result2.Succeeded);

            // Exactly one should have executed
            Assert.True(result1.ExecutorWasCalled ^ result2.ExecutorWasCalled);

            // Verify final database state
            var executionCount = await GetExecutionCount(testToken);
            Assert.Equal(1, executionCount);

            var finalState = await GetFinalState(testToken);
            Assert.Equal("current", finalState);
        }
        finally
        {
            await CleanupTestDatabase(testToken);
        }
    }

    private async Task InitializeTestDatabase(CancellationToken cancellationToken)
    {
        using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DROP TABLE IF EXISTS test_migration_state;
                CREATE TABLE test_migration_state (
                    state TEXT NOT NULL,
                    execution_count INT NOT NULL DEFAULT 0
                );
                INSERT INTO test_migration_state (state, execution_count) VALUES ('pending', 0);
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task CleanupTestDatabase(CancellationToken cancellationToken)
    {
        using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS test_migration_state;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task<string> GetFinalState(CancellationToken cancellationToken)
    {
        using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT state FROM test_migration_state LIMIT 1;";
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result?.ToString() ?? "unknown";
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task<int> GetExecutionCount(CancellationToken cancellationToken)
    {
        using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT execution_count FROM test_migration_state LIMIT 1;";
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is int count ? count : 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static bool ShouldRunPostgreSqlTests()
    {
        var envVar = Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS");
        return envVar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static string GetPostgresImage()
    {
        var envVar = Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE");
        return envVar ?? "postgres:15-alpine";
    }

    /// <summary>
    /// Real-database executor whose ExecuteAsync blocks on a shared gate after signaling that
    /// execution has started. Used to make double-instance concurrency deterministic instead of
    /// timing-dependent: the test controls exactly when the gated instance is allowed to finish
    /// its migration and release the advisory lock.
    /// </summary>
    private sealed class GatedRealDatabaseExecutor : IDatabaseMigrationExecutor
    {
        private readonly string connectionString;
        private readonly TaskCompletionSource<bool> executionStarted;
        private readonly TaskCompletionSource<bool> allowExecutionToComplete;

        public GatedRealDatabaseExecutor(
            string connectionString,
            TaskCompletionSource<bool> executionStarted,
            TaskCompletionSource<bool> allowExecutionToComplete)
        {
            this.connectionString = connectionString;
            this.executionStarted = executionStarted;
            this.allowExecutionToComplete = allowExecutionToComplete;
        }

        public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT state FROM test_migration_state LIMIT 1;";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                var state = result?.ToString();

                return state switch
                {
                    "pending" => MigrationObservationState.PendingMigration,
                    "current" => MigrationObservationState.CurrentVersionCompatible,
                    _ => MigrationObservationState.Empty,
                };
            }
            finally
            {
                await connection.CloseAsync();
            }
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Signal that some instance has entered ExecuteAsync (lock is held at this point),
            // then block until the test explicitly allows this execution to proceed.
            executionStarted.TrySetResult(true);
            await allowExecutionToComplete.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE test_migration_state
                    SET state = 'current', execution_count = execution_count + 1;
                ";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await connection.CloseAsync();
            }
        }
    }
}

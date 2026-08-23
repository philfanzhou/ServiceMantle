using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public class DatabaseMigrationOrchestratorTests
{
    private static readonly ServiceId TestServiceId = ServiceId.Parse("test-service");

    private static readonly BootstrapDatabaseConfiguration TestBootstrap =
        new("PostgreSQL", "15", "Host=localhost;Database=test;Username=user;Password=pass");

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OrchestrateMigration_WhenDatabaseIsCurrentVersion_SkipsExecutor()
    {
        var executor = new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible);
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(1, executor.InspectCallCount);
        Assert.Equal(0, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.AcquireAttempts);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenDatabaseIsEmpty_ExecutesAndSucceeds()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.Empty, MigrationObservationState.CurrentVersionCompatible });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(2, executor.InspectCallCount);
        Assert.Equal(1, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenPendingMigration_ExecutesAndSucceeds()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(1, executor.ExecuteCallCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenVersionTooNew_FailsWithoutExecutor()
    {
        var executor = new FakeMigrationExecutor(MigrationObservationState.VersionTooNew);
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.VersionTooNew, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(0, executor.ExecuteCallCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenInspectThrows_FailsSafely()
    {
        var executor = new FakeMigrationExecutor(
            MigrationObservationState.Empty,
            inspectException: new InvalidOperationException("Inspect failed"));
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.InspectionFailed, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.DoesNotContain("Inspect failed", result.ErrorMessage);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenExecuteThrows_FailsButMarksExecutorCalled()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration },
            executeException: new InvalidOperationException("Migration failed"));
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.ExecutionFailed, result.ErrorCode);
        Assert.True(result.ExecutorWasCalled);
        Assert.DoesNotContain("Migration failed", result.ErrorMessage);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenFinalInspectThrows_FailsButReleasesLeaseOnce()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration },
            inspectExceptionAtCall: new InvalidOperationException("Final inspect failed"),
            inspectExceptionCallNumber: 2);
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.InspectionFailed, result.ErrorCode);
        Assert.True(result.ExecutorWasCalled);
        Assert.DoesNotContain("Final inspect failed", result.ErrorMessage);
        Assert.Equal(1, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenCancelledDuringExecute_ThrowsAndReleasesLeaseOnce()
    {
        var executionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration },
            executeDelay: async ct =>
            {
                executionStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
            });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        using var cts = new CancellationTokenSource();

        var orchestrateTask = orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            cts.Token).AsTask();

        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrateTask);

        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenFinalStateInvalid_FailsButMarksExecutorCalled()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration, MigrationObservationState.PendingMigration });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.FinalStateInvalid, result.ErrorCode);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenLockNotSupported_FailsSafely()
    {
        var executor = new FakeMigrationExecutor();
        var registry = new DatabaseMigrationLockProviderRegistry([]); // No providers
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenProviderReturnsNullLease_FailsClosed()
    {
        var executor = new FakeMigrationExecutor();
        var lockProvider = new FakeMigrationLockProvider(returnNullLease: true);
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.DoesNotContain("Password", result.ErrorMessage);
        Assert.Equal(0, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenLockTimeout_ReturnsSafeError()
    {
        var executor = new FakeMigrationExecutor();
        var lockProvider = new FakeMigrationLockProvider(
            acquireException: new DatabaseMigrationLockException(
                WellKnownMigrationErrorCodes.LockTimeout,
                "Lock acquisition timed out."));
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenCallerCancels_ThrowsOperationCanceledException()
    {
        var executor = new FakeMigrationExecutor();
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                cts.Token).AsTask());

        Assert.NotNull(ex);
    }

    private static CancellationToken GetTestCancellationToken()
    {
        try
        {
            return TestContext.Current.CancellationToken;
        }
        catch
        {
            return CancellationToken.None;
        }
    }

    [Fact]
    public async Task OrchestrateMigration_InvalidTimeout_ThrowsArgumentException()
    {
        var executor = new FakeMigrationExecutor();
        var registry = new DatabaseMigrationLockProviderRegistry([]);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                TimeSpan.Zero,
                GetTestCancellationToken()).AsTask());

        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrchestrateMigration_TrueDoubleInstanceScenario_OnlyOneExecutes()
    {
        // Shared state: simulates a real database
        var sharedState = new SharedDatabaseState();

        // First instance
        var executor1 = new SharedStateExecutor(sharedState);
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider]);
        var orchestrator1 = new DatabaseMigrationOrchestrator(executor1, registry);

        // Second instance
        var executor2 = new SharedStateExecutor(sharedState);
        var orchestrator2 = new DatabaseMigrationOrchestrator(executor2, registry);

        // Run concurrently
        var task1 = orchestrator1.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        // Give first instance a tiny moment to acquire lock
        await Task.Delay(50, GetTestCancellationToken());

        var task2 = orchestrator2.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        var result1 = await task1;
        var result2 = await task2;

        // Both should succeed
        Assert.True(result1.Succeeded);
        Assert.True(result2.Succeeded);

        // Only one should have executed
        Assert.Equal(1, sharedState.ExecutionCount);

        // Second instance should have skipped execution
        Assert.True(result1.ExecutorWasCalled || result2.ExecutorWasCalled);
        if (result1.ExecutorWasCalled)
        {
            Assert.False(result2.ExecutorWasCalled);
        }
        else
        {
            Assert.True(result2.ExecutorWasCalled);
        }
    }

    /// <summary>
    /// Shared database state for testing multi-instance scenario.
    /// </summary>
    private sealed class SharedDatabaseState
    {
        private MigrationObservationState currentState = MigrationObservationState.PendingMigration;
        private readonly object lockObj = new();

        public int ExecutionCount { get; private set; }

        public MigrationObservationState GetCurrentState()
        {
            lock (lockObj)
            {
                return currentState;
            }
        }

        public void SetMigrationComplete()
        {
            lock (lockObj)
            {
                currentState = MigrationObservationState.CurrentVersionCompatible;
                ExecutionCount++;
            }
        }
    }

    /// <summary>
    /// Executor that uses shared database state.
    /// </summary>
    private sealed class SharedStateExecutor : IDatabaseMigrationExecutor
    {
        private readonly SharedDatabaseState sharedState;

        public SharedStateExecutor(SharedDatabaseState sharedState)
        {
            this.sharedState = sharedState;
        }

        public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValueTask.CompletedTask;
            return sharedState.GetCurrentState();
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100); // Simulate work
            sharedState.SetMigrationComplete();
        }
    }
}

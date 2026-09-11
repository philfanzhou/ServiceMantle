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

    [Theory]
    [InlineData(MigrationObservationState.Empty, true, null, true, 2, 1)]
    [InlineData(MigrationObservationState.PendingMigration, true, null, true, 2, 1)]
    [InlineData(MigrationObservationState.CurrentVersionCompatible, true, null, false, 1, 0)]
    [InlineData(MigrationObservationState.VersionTooNew, false, WellKnownMigrationErrorCodes.VersionTooNew, false, 1, 0)]
    [InlineData(MigrationObservationState.InspectionFailed, false, WellKnownMigrationErrorCodes.InspectionFailed, false, 1, 0)]
    [InlineData((MigrationObservationState)999, false, WellKnownMigrationErrorCodes.InspectionFailed, false, 1, 0)]
    public async Task Initial_state_matrix_only_executes_for_the_finite_allowed_set(
        MigrationObservationState initialState,
        bool expectedSucceeded,
        string? expectedErrorCode,
        bool expectedExecutorWasCalled,
        int expectedInspectCalls,
        int expectedExecuteCalls)
    {
        const string bootstrapSecret = "initial-state-bootstrap-secret";
        var bootstrap = new BootstrapDatabaseConfiguration(
            "PostgreSQL",
            "15",
            $"Host=localhost;Database=test;Username=user;Password={bootstrapSecret}");
        var executor = new FakeMigrationExecutor(
            [initialState, MigrationObservationState.CurrentVersionCompatible]);
        var lockProvider = new FakeMigrationLockProvider(
            disposeException: new InvalidOperationException("initial-state-release-secret"));
        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);

        var result = await new DatabaseMigrationOrchestrator(executor, registry).OrchestrateMigrationAsync(
            TestServiceId,
            bootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedSucceeded, result.Succeeded);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(expectedExecutorWasCalled, result.ExecutorWasCalled);
        Assert.Equal(expectedInspectCalls, executor.InspectCallCount);
        Assert.Equal(expectedExecuteCalls, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
        Assert.DoesNotContain(bootstrapSecret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(bootstrapSecret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("initial-state-release-secret", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("initial-state-release-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("999", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("999", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenDatabaseIsCurrentVersion_SkipsExecutor()
    {
        var executor = new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible);
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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

    [Theory]
    [InlineData(OrchestrationStage.LockAcquisition, WellKnownMigrationErrorCodes.LockFailed, false, 0, 0, 0)]
    [InlineData(OrchestrationStage.InitialInspection, WellKnownMigrationErrorCodes.InspectionFailed, false, 1, 0, 1)]
    [InlineData(OrchestrationStage.Execution, WellKnownMigrationErrorCodes.ExecutionFailed, true, 1, 1, 1)]
    [InlineData(OrchestrationStage.FinalInspection, WellKnownMigrationErrorCodes.InspectionFailed, true, 2, 1, 1)]
    public async Task Internal_cancellation_is_mapped_to_the_safe_stage_result(
        OrchestrationStage stage,
        string expectedErrorCode,
        bool expectedExecutorWasCalled,
        int expectedInspectCalls,
        int expectedExecuteCalls,
        int expectedDisposeCalls)
    {
        const string secret = "Host=private;Password=top-secret";
        var internalCancellation = new OperationCanceledException(secret);
        FakeMigrationExecutor executor;
        FakeMigrationLockProvider lockProvider;

        switch (stage)
        {
            case OrchestrationStage.LockAcquisition:
                executor = new FakeMigrationExecutor();
                lockProvider = new FakeMigrationLockProvider(acquireException: internalCancellation);
                break;
            case OrchestrationStage.InitialInspection:
                executor = new FakeMigrationExecutor(
                    MigrationObservationState.Empty,
                    inspectException: internalCancellation);
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            case OrchestrationStage.Execution:
                executor = new FakeMigrationExecutor(
                    [MigrationObservationState.PendingMigration],
                    executeException: internalCancellation);
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            case OrchestrationStage.FinalInspection:
                executor = new FakeMigrationExecutor(
                    [MigrationObservationState.PendingMigration],
                    inspectExceptionAtCall: internalCancellation,
                    inspectExceptionCallNumber: 2);
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }

        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);
        var result = await new DatabaseMigrationOrchestrator(executor, registry).OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(expectedExecutorWasCalled, result.ExecutorWasCalled);
        Assert.DoesNotContain(secret, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(expectedInspectCalls, executor.InspectCallCount);
        Assert.Equal(expectedExecuteCalls, executor.ExecuteCallCount);
        Assert.Equal(expectedDisposeCalls, lockProvider.LeaseDisposeCount);
    }

    [Theory]
    [InlineData(OrchestrationStage.LockAcquisition, StageCompletion.ThrowsCancellation)]
    [InlineData(OrchestrationStage.LockAcquisition, StageCompletion.ThrowsFailure)]
    [InlineData(OrchestrationStage.LockAcquisition, StageCompletion.Returns)]
    [InlineData(OrchestrationStage.InitialInspection, StageCompletion.ThrowsCancellation)]
    [InlineData(OrchestrationStage.InitialInspection, StageCompletion.ThrowsFailure)]
    [InlineData(OrchestrationStage.InitialInspection, StageCompletion.Returns)]
    [InlineData(OrchestrationStage.Execution, StageCompletion.ThrowsCancellation)]
    [InlineData(OrchestrationStage.Execution, StageCompletion.ThrowsFailure)]
    [InlineData(OrchestrationStage.Execution, StageCompletion.Returns)]
    [InlineData(OrchestrationStage.FinalInspection, StageCompletion.ThrowsCancellation)]
    [InlineData(OrchestrationStage.FinalInspection, StageCompletion.ThrowsFailure)]
    [InlineData(OrchestrationStage.FinalInspection, StageCompletion.Returns)]
    public async Task Caller_cancellation_has_priority_over_every_stage_outcome(
        OrchestrationStage stage,
        StageCompletion completion)
    {
        using var cancellation = new CancellationTokenSource();
        Exception? stageException = completion switch
        {
            StageCompletion.ThrowsCancellation => new OperationCanceledException(
                "Host=private;Password=top-secret"),
            StageCompletion.ThrowsFailure => new InvalidOperationException(
                "Host=private;Password=top-secret"),
            StageCompletion.Returns => null,
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        Task CancelCaller()
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        FakeMigrationExecutor executor;
        FakeMigrationLockProvider lockProvider;
        switch (stage)
        {
            case OrchestrationStage.LockAcquisition:
                executor = new FakeMigrationExecutor();
                lockProvider = new FakeMigrationLockProvider(
                    acquireException: stageException,
                    acquireDelay: _ => CancelCaller(),
                    ignoreCancellationAfterAcquireDelay: completion == StageCompletion.Returns,
                    disposeException: new InvalidOperationException("release secret"));
                break;
            case OrchestrationStage.InitialInspection:
                executor = new FakeMigrationExecutor(
                    MigrationObservationState.PendingMigration,
                    inspectException: stageException,
                    inspectDelay: (_, _) => CancelCaller());
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            case OrchestrationStage.Execution:
                executor = new FakeMigrationExecutor(
                    [MigrationObservationState.PendingMigration],
                    executeException: stageException,
                    executeDelay: _ => CancelCaller(),
                    ignoreCancellationAfterExecuteDelay: true);
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            case OrchestrationStage.FinalInspection:
                executor = new FakeMigrationExecutor(
                    [MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible],
                    inspectExceptionAtCall: stageException,
                    inspectExceptionCallNumber: 2,
                    inspectDelay: (call, _) => call == 2 ? CancelCaller() : Task.CompletedTask);
                lockProvider = new FakeMigrationLockProvider(
                    disposeException: new InvalidOperationException("release secret"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }

        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DatabaseMigrationOrchestrator(executor, registry).OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                cancellation.Token).AsTask());

        AssertSafeCallerCancellation(exception, cancellation.Token);
        var expectedDisposeCalls = stage == OrchestrationStage.LockAcquisition &&
            completion != StageCompletion.Returns
                ? 0
                : 1;
        Assert.Equal(expectedDisposeCalls, lockProvider.LeaseDisposeCount);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    public async Task Lease_loss_during_each_stage_fails_closed_and_starts_no_later_stage(
        int lostStage,
        int expectedInspectCalls,
        int expectedExecuteCalls)
    {
        var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeMigrationExecutor(
            [MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible],
            executeDelay: async token =>
            {
                if (lostStage == 1)
                {
                    stageStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            },
            inspectDelay: async (call, token) =>
            {
                if ((lostStage == 0 && call == 1) || (lostStage == 2 && call == 2))
                {
                    stageStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var orchestration = orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken).AsTask();
        await stageStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        lockProvider.LoseLease();
        var result = await orchestration.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.Equal(
            "The migration lock lease was lost before orchestration completed.",
            result.ErrorMessage);
        Assert.DoesNotContain("Password", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedExecuteCalls != 0, result.ExecutorWasCalled);
        Assert.Equal(expectedInspectCalls, executor.InspectCallCount);
        Assert.Equal(expectedExecuteCalls, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Theory]
    [InlineData(OrchestrationStage.InitialInspection, 1, 0)]
    [InlineData(OrchestrationStage.Execution, 1, 1)]
    [InlineData(OrchestrationStage.FinalInspection, 2, 1)]
    public async Task Internal_cancellation_with_lease_loss_is_mapped_to_lock_failure(
        OrchestrationStage stage,
        int expectedInspectCalls,
        int expectedExecuteCalls)
    {
        var lockProvider = new FakeMigrationLockProvider();
        var internalCancellation = new OperationCanceledException(
            "Host=private;Password=top-secret");
        FakeMigrationExecutor executor = stage switch
        {
            OrchestrationStage.InitialInspection => new FakeMigrationExecutor(
                MigrationObservationState.PendingMigration,
                inspectException: internalCancellation,
                inspectDelay: (_, _) =>
                {
                    lockProvider.LoseLease();
                    return Task.CompletedTask;
                }),
            OrchestrationStage.Execution => new FakeMigrationExecutor(
                [MigrationObservationState.PendingMigration],
                executeException: internalCancellation,
                executeDelay: _ =>
                {
                    lockProvider.LoseLease();
                    return Task.CompletedTask;
                }),
            OrchestrationStage.FinalInspection => new FakeMigrationExecutor(
                [MigrationObservationState.PendingMigration],
                inspectExceptionAtCall: internalCancellation,
                inspectExceptionCallNumber: 2,
                inspectDelay: (call, _) =>
                {
                    if (call == 2)
                    {
                        lockProvider.LoseLease();
                    }

                    return Task.CompletedTask;
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);

        var result = await new DatabaseMigrationOrchestrator(executor, registry).OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.Equal(expectedExecuteCalls != 0, result.ExecutorWasCalled);
        Assert.Equal(expectedInspectCalls, executor.InspectCallCount);
        Assert.Equal(expectedExecuteCalls, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task Caller_cancellation_takes_priority_when_it_races_with_lease_loss()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeMigrationExecutor(
            [MigrationObservationState.PendingMigration],
            executeDelay: async token =>
            {
                executionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry(
            [lockProvider],
            DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);
        using var cancellation = new CancellationTokenSource();

        var orchestration = orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            cancellation.Token).AsTask();
        await executionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        cancellation.Cancel();
        lockProvider.LoseLease();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => orchestration);
        AssertSafeCallerCancellation(exception, cancellation.Token);
        Assert.Equal(1, executor.InspectCallCount);
        Assert.Equal(1, executor.ExecuteCallCount);
        Assert.Equal(1, lockProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task OrchestrateMigration_WhenFinalStateInvalid_FailsButMarksExecutorCalled()
    {
        var executor = new FakeMigrationExecutor(
            new[] { MigrationObservationState.PendingMigration, MigrationObservationState.PendingMigration });
        var lockProvider = new FakeMigrationLockProvider();
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([], DatabaseProviderIdResolver.Empty); // No providers
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
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
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                cts.Token).AsTask());

        AssertSafeCallerCancellation(ex, cts.Token);
        Assert.Equal(0, lockProvider.AcquireAttempts);
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

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception,
        CancellationToken callerToken)
    {
        Assert.Equal("Migration orchestration was cancelled by the caller.", exception.Message);
        Assert.Equal(callerToken, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("Host=", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrchestrateMigration_InvalidTimeout_ThrowsArgumentException()
    {
        var executor = new FakeMigrationExecutor();
        var registry = new DatabaseMigrationLockProviderRegistry([], DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                TimeSpan.Zero,
                GetTestCancellationToken()).AsTask());

        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task OrchestrateMigration_TrueDoubleInstanceScenario_OnlyOneExecutes(
        int lockTimeoutSeconds)
    {
        // A legal finite positive budget. The scenario's own turn double never starts a real-time
        // acquisition timer, so the outcome cannot depend on the wall-clock budget: the short
        // budget and the original budget must produce the identical scenario.
        var lockTimeout = TimeSpan.FromSeconds(lockTimeoutSeconds);

        // Shared state: simulates a real database
        var sharedState = new SharedDatabaseState();

        // Both instances contend over one turn this test owns, modelled on a real SemaphoreSlim;
        // no other test can enter it.
        using var turn = new GatedMigrationTurn();

        // First instance
        var executor1 = new SharedStateExecutor(sharedState);
        var lockProvider1 = new GatedTurnLockProvider("PostgreSQL", turn);
        var registry1 = new DatabaseMigrationLockProviderRegistry([lockProvider1], DatabaseProviderIdResolver.Empty);
        var orchestrator1 = new DatabaseMigrationOrchestrator(executor1, registry1);

        // Second instance
        var executor2 = new SharedStateExecutor(sharedState);
        var lockProvider2 = new GatedTurnLockProvider("PostgreSQL", turn);
        var registry2 = new DatabaseMigrationLockProviderRegistry([lockProvider2], DatabaseProviderIdResolver.Empty);
        var orchestrator2 = new DatabaseMigrationOrchestrator(executor2, registry2);

        using var abort = new CancellationTokenSource();
        var instance1Started = false;
        var instance2Started = false;
        ValueTask<MigrationExecutionResult> task1 = default;
        ValueTask<MigrationExecutionResult> task2 = default;
        try
        {
            // Instance 1 takes the turn and stops inside its own locked execution, holding it.
            task1 = orchestrator1.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                lockTimeout,
                abort.Token);
            instance1Started = true;
            await sharedState.ExecuteEntered.Task.WaitAsync(GateTimeout, abort.Token);

            // Instance 2 cannot have entered any executor stage while instance 1 holds the turn.
            Assert.Equal(0, executor2.InspectCallCount);
            Assert.Equal(0, executor2.ExecuteCallCount);

            task2 = orchestrator2.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                lockTimeout,
                abort.Token);
            instance2Started = true;

            // Deterministic evidence of contention: instance 2's wait is committed on the
            // semaphore - it cannot complete synchronously while instance 1 holds the turn -
            // and the committed waiter has still not reached any executor stage.
            await turn.WaitCommitted.Task.WaitAsync(GateTimeout, abort.Token);
            Assert.Equal(0, executor2.InspectCallCount);
            Assert.Equal(0, executor2.ExecuteCallCount);

            // Release instance 1; both instances now run to completion.
            sharedState.ExecuteRelease.SetResult();

            var result1 = await task1.AsTask().WaitAsync(CompletionTimeout, abort.Token);
            var result2 = await task2.AsTask().WaitAsync(CompletionTimeout, abort.Token);

            // Both should succeed
            Assert.True(result1.Succeeded);
            Assert.True(result2.Succeeded);

            // Only one should have executed, and it is the instance that held the turn first.
            Assert.Equal(1, sharedState.ExecutionCount);
            Assert.True(result1.ExecutorWasCalled);
            Assert.False(result2.ExecutorWasCalled);

            // Both leases were released.
            Assert.Equal(2, turn.LeaseReleases);
        }
        finally
        {
            // An assertion failure must not leave waiters behind: open the execution gate,
            // cancel whatever has not finished, and drain both orchestrations.
            sharedState.ExecuteRelease.TrySetResult();
            await abort.CancelAsync();
            await SettleAsync(instance1Started, task1);
            await SettleAsync(instance2Started, task2);
        }
    }

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private static async ValueTask SettleAsync(
        bool started,
        ValueTask<MigrationExecutionResult> orchestration)
    {
        if (!started)
        {
            return;
        }

        try
        {
            await orchestration;
        }
        catch
        {
            // The cleanup pass already knows the scenario's outcome; a cancelled or failed
            // orchestration adds nothing to it.
        }
    }

    /// <summary>
    /// Shared database state for testing multi-instance scenario. Its gates let the test hold the
    /// first instance inside the locked execution and release it only after the second instance's
    /// committed lock wait is proven, with no wall-clock delay in between.
    /// </summary>
    private sealed class SharedDatabaseState
    {
        private MigrationObservationState currentState = MigrationObservationState.PendingMigration;
        private readonly object lockObj = new();

        internal TaskCompletionSource ExecuteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ExecuteRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// Executor that uses shared database state. Its execution signals the test when the locked
    /// work was entered and waits for the test's release instead of sleeping, so the double-
    /// instance ordering is driven by test-controlled signals only.
    /// </summary>
    private sealed class SharedStateExecutor : IDatabaseMigrationExecutor
    {
        private readonly SharedDatabaseState sharedState;

        public SharedStateExecutor(SharedDatabaseState sharedState)
        {
            this.sharedState = sharedState;
        }

        public int InspectCallCount { get; private set; }

        public int ExecuteCallCount { get; private set; }

        public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCallCount++;
            await ValueTask.CompletedTask;
            return sharedState.GetCurrentState();
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCallCount++;
            sharedState.ExecuteEntered.TrySetResult();
            await sharedState.ExecuteRelease.Task.WaitAsync(GateTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            sharedState.SetMigrationComplete();
        }
    }

    /// <summary>
    /// The scenario's own turn, modelled on a real <see cref="SemaphoreSlim"/>: mutual exclusion
    /// and release only. It never starts a real-time acquisition timer, so the scenario's success
    /// cannot depend on a wall-clock budget. <see cref="WaitCommitted"/> fires only after a
    /// contended wait was actually committed on the semaphore, never at the acquire entry.
    /// </summary>
    private sealed class GatedMigrationTurn : IDisposable
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal TaskCompletionSource WaitCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int LeaseReleases => Volatile.Read(ref leaseReleases);

        private int leaseReleases;

        internal void RecordRelease() => Interlocked.Increment(ref leaseReleases);

        public void Dispose() => Gate.Dispose();
    }

    /// <summary>
    /// A lock double private to the double-instance scenario. It hands out leases over the test's
    /// own <see cref="GatedMigrationTurn"/> and waits on the caller's token alone; the finite
    /// acquisition timeout it receives is validated by the production orchestrator but never
    /// starts a timer here, so this double tests no provider timeout contract.
    /// </summary>
    private sealed class GatedTurnLockProvider(string providerId, GatedMigrationTurn turn)
        : IDatabaseMigrationLockProvider
    {
        public string ProviderId { get; } = providerId ?? throw new ArgumentNullException(nameof(providerId));

        public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(bootstrap);
            cancellationToken.ThrowIfCancellationRequested();

            var wait = turn.Gate.WaitAsync(cancellationToken);
            if (wait.IsCompleted)
            {
                // The holder completed its wait without contention; nothing to commit.
                await wait.ConfigureAwait(false);
            }
            else
            {
                // The waiter is now committed on the semaphore's wait queue; only at this point
                // may the test treat the contention as proven.
                turn.WaitCommitted.TrySetResult();
                await wait.WaitAsync(GateTimeout, cancellationToken).ConfigureAwait(false);
            }

            return new GatedTurnLease(ProviderId, turn);
        }

        private sealed class GatedTurnLease(string providerId, GatedMigrationTurn turn)
            : IDatabaseMigrationLock
        {
            private int disposed;

            public string ProviderId { get; } = providerId;

            public CancellationToken LeaseLost => CancellationToken.None;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    turn.RecordRelease();
                    turn.Gate.Release();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    public enum OrchestrationStage
    {
        LockAcquisition,
        InitialInspection,
        Execution,
        FinalInspection
    }

    public enum StageCompletion
    {
        ThrowsCancellation,
        ThrowsFailure,
        Returns
    }
}

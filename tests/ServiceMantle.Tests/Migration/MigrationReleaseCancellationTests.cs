using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Covers the release-settled completion checkpoint of the real-lease orchestration: caller
/// cancellation observed by the time the acquired lease's release settles must take precedence
/// over delivering the primary result. All doubles are private to this file.
/// </summary>
public class MigrationReleaseCancellationTests
{
    private const string FixedCallerCancellationMessage =
        "Migration orchestration was cancelled by the caller.";
    private const string BootstrapSecret = "release-check-bootstrap-secret";
    private const string ReleaseFailureSecret = "release-settlement-secret";
    private const string ReleaseInternalCancellationSecret = "release-internal-cancellation-secret";
    private const string StageSecret = "stage-failure-secret";

    private static readonly ServiceId TestServiceId = ServiceId.Parse("test-service");

    private static readonly BootstrapDatabaseConfiguration TestBootstrap =
        new("PostgreSQL", "15", $"Host=localhost;Database=test;Username=user;Password={BootstrapSecret}");

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.ThrowsInternalCancellation)]
    public async Task Caller_cancellation_during_release_suppresses_every_primary_outcome(
        OrchestrationOutcome outcome,
        ReleaseSettlement settlement)
    {
        using var callerCancellation = new CancellationTokenSource();
        var scenario = Scenario.Create(outcome, settlement, callerCancellation, cancelCallerOnRelease: true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scenario.Orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                callerCancellation.Token).AsTask());

        AssertSafeCallerCancellation(exception, callerCancellation.Token);
        Assert.Equal(1, scenario.Lease.DisposeCount);
        Assert.Equal(scenario.ExpectedInspectCalls, scenario.Executor.InspectCallCount);
        Assert.Equal(scenario.ExpectedExecuteCalls, scenario.Executor.ExecuteCallCount);
        Assert.Equal(scenario.ExpectedAcquireCalls, scenario.Provider.AcquireCount);
    }

    [Theory]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.SkippedSuccess, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.ExecutedSuccess, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.VersionTooNew, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.InitialInspectionFailed, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.ExecutionFailed, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.FinalStateInvalid, ReleaseSettlement.ThrowsInternalCancellation)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.Completes)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.ThrowsFailure)]
    [InlineData(OrchestrationOutcome.LeaseLossDetected, ReleaseSettlement.ThrowsInternalCancellation)]
    public async Task Uncancelled_control_preserves_the_primary_outcome_for_every_release_settlement(
        OrchestrationOutcome outcome,
        ReleaseSettlement settlement)
    {
        var scenario = Scenario.Create(outcome, settlement, callerCancellation: null, cancelCallerOnRelease: false);

        var result = await scenario.Orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(scenario.ExpectedSucceeded, result.Succeeded);
        Assert.Equal(scenario.ExpectedErrorCode, result.ErrorCode);
        Assert.Equal(scenario.ExpectedExecutorWasCalled, result.ExecutorWasCalled);
        Assert.Equal(scenario.ExpectedInspectCalls, scenario.Executor.InspectCallCount);
        Assert.Equal(scenario.ExpectedExecuteCalls, scenario.Executor.ExecuteCallCount);
        Assert.Equal(1, scenario.Lease.DisposeCount);
        Assert.DoesNotContain(BootstrapSecret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapSecret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(StageSecret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleaseFailureSecret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ReleaseInternalCancellationSecret,
            result.ErrorMessage ?? string.Empty,
            StringComparison.Ordinal);

        if (outcome == OrchestrationOutcome.LeaseLossDetected)
        {
            // Explicit lease loss keeps failing closed; a normal release is never reclassified
            // as lease loss, which the non-lease-loss rows above prove by their own error codes.
            Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        }
        else
        {
            Assert.NotEqual(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        }
    }

    [Theory]
    [InlineData(ReleaseSettlement.Completes)]
    [InlineData(ReleaseSettlement.ThrowsFailure)]
    [InlineData(ReleaseSettlement.ThrowsInternalCancellation)]
    public async Task Caller_cancellation_from_an_earlier_stage_still_wins_after_release(
        ReleaseSettlement settlement)
    {
        using var callerCancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor(
            [MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible],
            inspectHook: call =>
            {
                if (call == 1)
                {
                    callerCancellation.Cancel();
                }
            });
        var lease = new ReleaseSettlingLease(callerCancellation, settlement, cancelCallerOnRelease: false);
        var provider = new ScriptedLockProvider(lease);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, Registry(provider));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                callerCancellation.Token).AsTask());

        AssertSafeCallerCancellation(exception, callerCancellation.Token);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, executor.InspectCallCount);
        Assert.Equal(0, executor.ExecuteCallCount);
    }

    [Fact]
    public async Task Precancelled_caller_never_acquires_or_releases()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var scenario = Scenario.Create(
            OrchestrationOutcome.SkippedSuccess,
            ReleaseSettlement.Completes,
            callerCancellation,
            cancelCallerOnRelease: true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scenario.Orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DefaultLockTimeout,
                callerCancellation.Token).AsTask());

        AssertSafeCallerCancellation(exception, callerCancellation.Token);
        Assert.Equal(0, scenario.Provider.AcquireCount);
        Assert.Equal(0, scenario.Lease.DisposeCount);
        Assert.Equal(0, scenario.Executor.InspectCallCount);
        Assert.Equal(0, scenario.Executor.ExecuteCallCount);
    }

    [Fact]
    public async Task Failed_acquisition_without_lease_never_releases()
    {
        using var callerCancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor([MigrationObservationState.CurrentVersionCompatible]);
        var lease = new ReleaseSettlingLease(callerCancellation, ReleaseSettlement.Completes, cancelCallerOnRelease: true);
        var provider = new ScriptedLockProvider(
            lease,
            acquireException: new DatabaseMigrationLockException(
                WellKnownMigrationErrorCodes.LockTimeout,
                "Lock acquisition timed out."));
        var orchestrator = new DatabaseMigrationOrchestrator(executor, Registry(provider));

        var result = await orchestrator.OrchestrateMigrationAsync(
            TestServiceId,
            TestBootstrap,
            DefaultLockTimeout,
            callerCancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, result.ErrorCode);
        Assert.Equal(1, provider.AcquireCount);
        Assert.Equal(0, lease.DisposeCount);
        Assert.Equal(0, executor.InspectCallCount);
    }

    [Fact]
    public async Task Cancelling_one_orchestration_does_not_pollute_an_independent_one()
    {
        using var cancelledCaller = new CancellationTokenSource();
        var cancelledScenario = Scenario.Create(
            OrchestrationOutcome.ExecutedSuccess,
            ReleaseSettlement.ThrowsFailure,
            cancelledCaller,
            cancelCallerOnRelease: true);
        var independentScenario = Scenario.Create(
            OrchestrationOutcome.ExecutedSuccess,
            ReleaseSettlement.Completes,
            callerCancellation: null,
            cancelCallerOnRelease: false);

        var cancelledTask = cancelledScenario.Orchestrator.OrchestrateMigrationAsync(
            TestServiceId, TestBootstrap, DefaultLockTimeout, cancelledCaller.Token).AsTask();
        var independentTask = independentScenario.Orchestrator.OrchestrateMigrationAsync(
            TestServiceId, TestBootstrap, DefaultLockTimeout, TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAll(
            AssertSafeCallerCancellationAsync(cancelledTask, cancelledCaller.Token),
            AssertIndependentSuccessAsync(independentTask));

        Assert.Equal(1, cancelledScenario.Lease.DisposeCount);
        Assert.Equal(1, independentScenario.Lease.DisposeCount);
        Assert.Equal(1, cancelledScenario.Executor.ExecuteCallCount);
        Assert.Equal(1, independentScenario.Executor.ExecuteCallCount);
    }

    [Theory]
    [InlineData(ReleaseSettlement.Completes)]
    [InlineData(ReleaseSettlement.ThrowsFailure)]
    [InlineData(ReleaseSettlement.ThrowsInternalCancellation)]
    public async Task Explicit_multi_instance_delegation_observes_the_same_release_checkpoint(
        ReleaseSettlement settlement)
    {
        using var callerCancellation = new CancellationTokenSource();
        var scenario = Scenario.Create(
            OrchestrationOutcome.ExecutedSuccess,
            settlement,
            callerCancellation,
            cancelCallerOnRelease: true);
        var capabilities = new DatabaseDeploymentCapabilityRegistry(
            [new MultiInstanceCapabilityProvider()],
            DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(
            scenario.Executor,
            Registry(scenario.Provider),
            capabilities);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.OrchestrateMigrationAsync(
                TestServiceId,
                TestBootstrap,
                DatabaseDeploymentMode.MultiInstance,
                DefaultLockTimeout,
                callerCancellation.Token).AsTask());

        AssertSafeCallerCancellation(exception, callerCancellation.Token);
        Assert.Equal(1, scenario.Lease.DisposeCount);
        Assert.Equal(2, scenario.Executor.InspectCallCount);
        Assert.Equal(1, scenario.Executor.ExecuteCallCount);
    }

    private static DatabaseMigrationLockProviderRegistry Registry(ScriptedLockProvider provider) =>
        new([provider], DatabaseProviderIdResolver.Empty);

    private static async Task AssertSafeCallerCancellationAsync(
        Task<MigrationExecutionResult> orchestration,
        CancellationToken callerToken)
    {
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => orchestration);
        AssertSafeCallerCancellation(exception, callerToken);
    }

    private static async Task AssertIndependentSuccessAsync(Task<MigrationExecutionResult> orchestration)
    {
        var result = await orchestration;
        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
    }

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception,
        CancellationToken callerToken)
    {
        Assert.Equal(FixedCallerCancellationMessage, exception.Message);
        Assert.Equal(callerToken, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(BootstrapSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StageSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleaseFailureSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ReleaseInternalCancellationSecret,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public enum OrchestrationOutcome
    {
        SkippedSuccess,
        ExecutedSuccess,
        VersionTooNew,
        InitialInspectionFailed,
        ExecutionFailed,
        FinalStateInvalid,
        LeaseLossDetected
    }

    public enum ReleaseSettlement
    {
        Completes,
        ThrowsFailure,
        ThrowsInternalCancellation
    }

    private sealed class Scenario
    {
        private Scenario(
            DatabaseMigrationOrchestrator orchestrator,
            ScriptedExecutor executor,
            ScriptedLockProvider provider,
            ReleaseSettlingLease lease,
            bool expectedSucceeded,
            string? expectedErrorCode,
            bool expectedExecutorWasCalled,
            int expectedInspectCalls,
            int expectedExecuteCalls)
        {
            Orchestrator = orchestrator;
            Executor = executor;
            Provider = provider;
            Lease = lease;
            ExpectedSucceeded = expectedSucceeded;
            ExpectedErrorCode = expectedErrorCode;
            ExpectedExecutorWasCalled = expectedExecutorWasCalled;
            ExpectedInspectCalls = expectedInspectCalls;
            ExpectedExecuteCalls = expectedExecuteCalls;
        }

        internal DatabaseMigrationOrchestrator Orchestrator { get; }

        internal ScriptedExecutor Executor { get; }

        internal ScriptedLockProvider Provider { get; }

        internal ReleaseSettlingLease Lease { get; }

        internal bool ExpectedSucceeded { get; }

        internal string? ExpectedErrorCode { get; }

        internal bool ExpectedExecutorWasCalled { get; }

        internal int ExpectedInspectCalls { get; }

        internal int ExpectedExecuteCalls { get; }

        internal int ExpectedAcquireCalls => 1;

        internal static Scenario Create(
            OrchestrationOutcome outcome,
            ReleaseSettlement settlement,
            CancellationTokenSource? callerCancellation,
            bool cancelCallerOnRelease)
        {
            ScriptedExecutor executor;
            bool expectedSucceeded;
            string? expectedErrorCode;
            bool expectedExecutorWasCalled;
            int expectedInspectCalls;
            int expectedExecuteCalls;

            switch (outcome)
            {
                case OrchestrationOutcome.SkippedSuccess:
                    executor = new ScriptedExecutor([MigrationObservationState.CurrentVersionCompatible]);
                    expectedSucceeded = true;
                    expectedErrorCode = null;
                    expectedExecutorWasCalled = false;
                    expectedInspectCalls = 1;
                    expectedExecuteCalls = 0;
                    break;
                case OrchestrationOutcome.ExecutedSuccess:
                    executor = new ScriptedExecutor(
                        [MigrationObservationState.Empty, MigrationObservationState.CurrentVersionCompatible]);
                    expectedSucceeded = true;
                    expectedErrorCode = null;
                    expectedExecutorWasCalled = true;
                    expectedInspectCalls = 2;
                    expectedExecuteCalls = 1;
                    break;
                case OrchestrationOutcome.VersionTooNew:
                    executor = new ScriptedExecutor([MigrationObservationState.VersionTooNew]);
                    expectedSucceeded = false;
                    expectedErrorCode = WellKnownMigrationErrorCodes.VersionTooNew;
                    expectedExecutorWasCalled = false;
                    expectedInspectCalls = 1;
                    expectedExecuteCalls = 0;
                    break;
                case OrchestrationOutcome.InitialInspectionFailed:
                    executor = new ScriptedExecutor(
                        [MigrationObservationState.Empty],
                        inspectException: new InvalidOperationException(StageSecret));
                    expectedSucceeded = false;
                    expectedErrorCode = WellKnownMigrationErrorCodes.InspectionFailed;
                    expectedExecutorWasCalled = false;
                    expectedInspectCalls = 1;
                    expectedExecuteCalls = 0;
                    break;
                case OrchestrationOutcome.ExecutionFailed:
                    executor = new ScriptedExecutor(
                        [MigrationObservationState.PendingMigration],
                        executeException: new InvalidOperationException(StageSecret));
                    expectedSucceeded = false;
                    expectedErrorCode = WellKnownMigrationErrorCodes.ExecutionFailed;
                    expectedExecutorWasCalled = true;
                    expectedInspectCalls = 1;
                    expectedExecuteCalls = 1;
                    break;
                case OrchestrationOutcome.FinalStateInvalid:
                    executor = new ScriptedExecutor(
                        [MigrationObservationState.Empty, MigrationObservationState.PendingMigration]);
                    expectedSucceeded = false;
                    expectedErrorCode = WellKnownMigrationErrorCodes.FinalStateInvalid;
                    expectedExecutorWasCalled = true;
                    expectedInspectCalls = 2;
                    expectedExecuteCalls = 1;
                    break;
                case OrchestrationOutcome.LeaseLossDetected:
                    expectedSucceeded = false;
                    expectedErrorCode = WellKnownMigrationErrorCodes.LockFailed;
                    expectedExecutorWasCalled = false;
                    expectedInspectCalls = 1;
                    expectedExecuteCalls = 0;
                    executor = null!;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            var lease = new ReleaseSettlingLease(callerCancellation, settlement, cancelCallerOnRelease);
            if (outcome == OrchestrationOutcome.LeaseLossDetected)
            {
                executor = new ScriptedExecutor(
                    [MigrationObservationState.PendingMigration],
                    inspectHook: call =>
                    {
                        if (call == 1)
                        {
                            lease.LoseLease();
                        }
                    });
            }

            var provider = new ScriptedLockProvider(lease);
            var orchestrator = new DatabaseMigrationOrchestrator(executor, Registry(provider));
            return new Scenario(
                orchestrator,
                executor,
                provider,
                lease,
                expectedSucceeded,
                expectedErrorCode,
                expectedExecutorWasCalled,
                expectedInspectCalls,
                expectedExecuteCalls);
        }
    }

    private sealed class ScriptedExecutor(
        IReadOnlyList<MigrationObservationState> inspectStates,
        Exception? inspectException = null,
        Exception? executeException = null,
        Action<int>? inspectHook = null) : IDatabaseMigrationExecutor
    {
        internal int InspectCallCount { get; private set; }

        internal int ExecuteCallCount { get; private set; }

        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCallCount++;
            inspectHook?.Invoke(InspectCallCount);
            if (inspectException is not null)
            {
                return ValueTask.FromException<MigrationObservationState>(inspectException);
            }

            var state = inspectStates[Math.Min(InspectCallCount - 1, inspectStates.Count - 1)];
            return ValueTask.FromResult(state);
        }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCallCount++;
            return executeException is not null
                ? ValueTask.FromException(executeException)
                : ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedLockProvider(ReleaseSettlingLease lease, Exception? acquireException = null)
        : IDatabaseMigrationLockProvider
    {
        internal int AcquireCount { get; private set; }

        public string ProviderId => "PostgreSQL";

        public ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (acquireException is not null)
            {
                return ValueTask.FromException<IDatabaseMigrationLock>(acquireException);
            }

            return ValueTask.FromResult<IDatabaseMigrationLock>(lease);
        }
    }

    private sealed class ReleaseSettlingLease(
        CancellationTokenSource? callerCancellation,
        ReleaseSettlement settlement,
        bool cancelCallerOnRelease) : IDatabaseMigrationLock
    {
        private readonly CancellationTokenSource leaseLostSource = new();
        private int disposeCount;

        internal int DisposeCount => Volatile.Read(ref disposeCount);

        public string ProviderId => "PostgreSQL";

        public CancellationToken LeaseLost => leaseLostSource.Token;

        internal void LoseLease() => leaseLostSource.Cancel();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            if (cancelCallerOnRelease)
            {
                callerCancellation?.Cancel();
            }

            return settlement switch
            {
                ReleaseSettlement.Completes => ValueTask.CompletedTask,
                ReleaseSettlement.ThrowsFailure => ValueTask.FromException(
                    new InvalidOperationException(ReleaseFailureSecret)),
                ReleaseSettlement.ThrowsInternalCancellation => ValueTask.FromException(
                    new OperationCanceledException(
                        $"Host=private;Password={ReleaseInternalCancellationSecret}")),
                _ => throw new ArgumentOutOfRangeException(nameof(settlement))
            };
        }
    }

    private sealed class MultiInstanceCapabilityProvider : IDatabaseDeploymentCapabilityProvider
    {
        public DatabaseDeploymentCapability Capability { get; } =
            new("PostgreSQL", DatabaseDeploymentSupport.SingleAndMultiInstance);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MultiInstance delegation must not resolve target identity.");
    }
}

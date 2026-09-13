using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Installation;

/// <summary>
/// Covers the failure-cleanup completion checkpoint of <see cref="ServiceSetupOrchestrator"/>:
/// caller cancellation observed by the time the discard and the existing cleanliness recheck have
/// settled must take precedence over every failure classification. All doubles are private to
/// this file; shared fixtures are not modified.
/// </summary>
public sealed class SetupCleanupCancellationTests
{
    private const string FixedCallerCancellationMessage = "Service setup was cancelled by the caller.";
    private const string ProductRejectedCode = "product.rejected";
    private const string ValidationSecret = "Server=db;Password=validate-failure-secret";
    private const string RegistrationSecret = "Server=db;Password=register-failure-secret";
    private const string InternalCancellationSecret = "Host=private;Password=internal-cancellation-secret";
    private const string InternalCancellationInnerSecret = "internal-cancellation-inner-secret";
    private const string DiscardFailureSecret = "Server=db;Password=discard-failure-secret";
    private const string DiscardInternalSecret = "Host=private;Password=discard-internal-secret";
    private const string DiscardInnerSecret = "discard-internal-inner-secret";
    private const string RecheckSecret = "Server=db;Password=recheck-failure-secret";

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    public static TheoryData<FailureEntry, DiscardSettlement> EntrySettlementMatrix
    {
        get
        {
            var data = new TheoryData<FailureEntry, DiscardSettlement>();
            foreach (var entry in Enum.GetValues<FailureEntry>())
            {
                if (entry == FailureEntry.HealthyControl)
                {
                    continue;
                }

                foreach (var settlement in Enum.GetValues<DiscardSettlement>())
                {
                    data.Add(entry, settlement);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EntrySettlementMatrix))]
    public async Task Caller_cancellation_during_failure_cleanup_suppresses_every_classification(
        FailureEntry entry,
        DiscardSettlement settlement)
    {
        using var callerCancellation = new CancellationTokenSource();
        var scenario = Scenario.Build(entry, settlement, callerCancellation, cancelCallerOnDiscard: true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scenario.Orchestrator.OrchestrateAsync(callerCancellation.Token).AsTask());

        Assert.Equal(FixedCallerCancellationMessage, exception.Message);
        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertFreeOfSecrets(exception.Message);
        AssertFreeOfSecrets(exception.ToString());

        // Cleanup used CancellationToken.None exactly once; no compensating second discard.
        Assert.Equal(1, scenario.Scope.DiscardCount);
        Assert.Equal(CancellationToken.None, scenario.Scope.LastDiscardToken);

        // No later contributor registration was started after cancellation was observed.
        Assert.DoesNotContain("register:following", scenario.Calls);
        Assert.Equal(0, scenario.Following.RegistrationCount);
    }

    [Theory]
    [MemberData(nameof(EntrySettlementMatrix))]
    public async Task Uncancelled_control_preserves_classification_and_cleanup_priority(
        FailureEntry entry,
        DiscardSettlement settlement)
    {
        var scenario = Scenario.Build(entry, settlement, callerCancellation: null, cancelCallerOnDiscard: false);

        var result = await scenario.Orchestrator.OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExpectedUncancelledCode(entry, settlement), result.ErrorCode);
        Assert.Equal(1, scenario.Scope.DiscardCount);
        Assert.Equal(CancellationToken.None, scenario.Scope.LastDiscardToken);
        Assert.DoesNotContain("register:following", scenario.Calls);
        Assert.Equal(0, scenario.Following.RegistrationCount);
        AssertFreeOfSecrets(result.ToString());
    }

    [Fact]
    public async Task Precancelled_caller_invokes_no_contributor_and_no_cleanup()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var scenario = Scenario.Build(
            FailureEntry.RegistrationRejected,
            DiscardSettlement.CompletesClean,
            callerCancellation,
            cancelCallerOnDiscard: true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scenario.Orchestrator.OrchestrateAsync(callerCancellation.Token).AsTask());

        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
        Assert.Empty(scenario.Calls);
        Assert.Equal(0, scenario.Failing.ValidationCount);
        Assert.Equal(0, scenario.Failing.RegistrationCount);
        Assert.Equal(0, scenario.Scope.DiscardCount);
    }

    [Fact]
    public async Task Dirty_entry_scope_is_rejected_without_clearing_existing_caller_work()
    {
        var scenario = Scenario.Build(
            FailureEntry.RegistrationRejected,
            DiscardSettlement.CompletesClean,
            callerCancellation: null,
            cancelCallerOnDiscard: false);
        scenario.Scope.HasPendingChanges = true;

        var result = await scenario.Orchestrator.OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownSetupCodeErrorCodes.DirtyContext, result.ErrorCode);
        Assert.Equal(0, scenario.Scope.DiscardCount);
        Assert.True(scenario.Scope.HasPendingChanges);
        Assert.Empty(scenario.Calls);
    }

    [Fact]
    public async Task Success_path_adds_no_cleanup_and_keeps_stable_order()
    {
        var scenario = Scenario.Build(
            FailureEntry.HealthyControl,
            DiscardSettlement.CompletesClean,
            callerCancellation: null,
            cancelCallerOnDiscard: false);

        var result = await scenario.Orchestrator.OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, scenario.Scope.DiscardCount);
        Assert.Equal(
            ["validate:failing", "validate:following", "register:failing", "register:following"],
            scenario.Calls);
    }

    [Fact]
    public async Task Concurrent_orchestrations_overlap_and_cancel_one_without_polluting_the_other()
    {
        using var cancelledCaller = new CancellationTokenSource();
        using var gate = new AsyncGate();

        // The cancelled orchestration's discard would fail (CleanupFailed were it not cancelled) and
        // the independent one cleans up cleanly (product.rejected). Both park on the shared gate
        // inside DiscardPendingChangesAsync, so they are genuinely in flight at the same time before
        // either settles. The caller is cancelled by the test, not from inside the discard.
        var cancelledScenario = Scenario.Build(
            FailureEntry.RegistrationRejected,
            DiscardSettlement.ThrowsFailure,
            cancelledCaller,
            cancelCallerOnDiscard: false,
            gate);
        var independentScenario = Scenario.Build(
            FailureEntry.RegistrationRejected,
            DiscardSettlement.CompletesClean,
            callerCancellation: null,
            cancelCallerOnDiscard: false,
            gate);

        var cancelledTask = cancelledScenario.Orchestrator
            .OrchestrateAsync(cancelledCaller.Token).AsTask();
        var independentTask = independentScenario.Orchestrator
            .OrchestrateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            // Both orchestrations have entered failure cleanup (one discard each) and are parked there.
            await gate.WaitForArrivalsAsync(2, Observation, TestContext.Current.CancellationToken);
            Assert.False(cancelledTask.IsCompleted);
            Assert.False(independentTask.IsCompleted);
            Assert.Equal(1, cancelledScenario.Scope.DiscardCount);
            Assert.Equal(1, independentScenario.Scope.DiscardCount);

            // Cancel exactly one caller while both cleanups are still in flight, then release both.
            await cancelledCaller.CancelAsync();
        }
        finally
        {
            gate.Release();
        }

        var cancelledException = await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancelledTask);
        Assert.Equal(FixedCallerCancellationMessage, cancelledException.Message);
        Assert.Equal(cancelledCaller.Token, cancelledException.CancellationToken);
        Assert.Null(cancelledException.InnerException);
        AssertFreeOfSecrets(cancelledException.Message);
        AssertFreeOfSecrets(cancelledException.ToString());

        var independentResult = await independentTask;
        Assert.False(independentResult.Succeeded);
        Assert.Equal(ProductRejectedCode, independentResult.ErrorCode);
        AssertFreeOfSecrets(independentResult.ToString());

        // Each scope cleaned up exactly once with CancellationToken.None; no cross-contamination.
        Assert.Equal(1, cancelledScenario.Scope.DiscardCount);
        Assert.Equal(1, independentScenario.Scope.DiscardCount);
        Assert.Equal(CancellationToken.None, cancelledScenario.Scope.LastDiscardToken);
        Assert.Equal(CancellationToken.None, independentScenario.Scope.LastDiscardToken);
        Assert.True(cancelledCaller.IsCancellationRequested);
    }

    public static TheoryData<RecheckSettlement> RecheckSettlements => new()
    {
        RecheckSettlement.ReturnsClean,
        RecheckSettlement.ReturnsStillDirty,
        RecheckSettlement.ThrowsAfterCancellation,
    };

    [Theory]
    [MemberData(nameof(RecheckSettlements))]
    public async Task Caller_cancellation_during_cleanliness_recheck_suppresses_classification(
        RecheckSettlement recheckSettlement)
    {
        using var callerCancellation = new CancellationTokenSource();
        var scope = new RecheckCancellingStagingScope(callerCancellation, recheckSettlement);
        var calls = new List<string>();
        var failing = new ScriptedContributor(1, "failing", calls)
        {
            RegistrationResult = ServiceSetupContributorResult.Rejected(ProductRejectedCode),
        };
        var following = new ScriptedContributor(2, "following", calls);
        var orchestrator = new ServiceSetupOrchestrator([failing, following], scope);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.OrchestrateAsync(callerCancellation.Token).AsTask());

        // The discard completed cleanly first; the caller was cancelled only inside the cleanliness
        // recheck getter, yet that cancellation still outranks the product.rejected / CleanupFailed
        // classification and is delivered as a fresh, safe OperationCanceledException.
        Assert.Equal(FixedCallerCancellationMessage, exception.Message);
        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertFreeOfSecrets(exception.Message);
        AssertFreeOfSecrets(exception.ToString());

        // Cleanup used CancellationToken.None exactly once; the recheck cancelled exactly once.
        Assert.Equal(1, scope.DiscardCount);
        Assert.Equal(CancellationToken.None, scope.LastDiscardToken);
        Assert.Equal(1, scope.RecheckCancellationCount);

        // No later contributor registration was started after cancellation was observed.
        Assert.DoesNotContain("register:following", calls);
        Assert.Equal(0, following.RegistrationCount);
    }

    private static string ExpectedUncancelledCode(FailureEntry entry, DiscardSettlement settlement) =>
        settlement == DiscardSettlement.CompletesClean
            ? OriginalCode(entry)
            : WellKnownServiceSetupErrorCodes.CleanupFailed;

    private static string OriginalCode(FailureEntry entry) => entry switch
    {
        FailureEntry.ValidationSideEffect => WellKnownServiceSetupErrorCodes.ValidationSideEffect,
        FailureEntry.ValidationThrewWithDirtyScope => WellKnownServiceSetupErrorCodes.ContributorFailed,
        FailureEntry.RegistrationRejected => ProductRejectedCode,
        FailureEntry.RegistrationNull => WellKnownServiceSetupErrorCodes.ContributorFailed,
        FailureEntry.RegistrationThrewFailure => WellKnownServiceSetupErrorCodes.ContributorFailed,
        FailureEntry.RegistrationThrewInternalCancellation => WellKnownServiceSetupErrorCodes.ContributorFailed,
        FailureEntry.HealthyControl => throw new ArgumentOutOfRangeException(nameof(entry)),
        _ => throw new ArgumentOutOfRangeException(nameof(entry))
    };

    private static void AssertFreeOfSecrets(string text)
    {
        Assert.DoesNotContain(ValidationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RegistrationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationInnerSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscardFailureSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscardInternalSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscardInnerSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RecheckSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    public enum FailureEntry
    {
        ValidationSideEffect,
        ValidationThrewWithDirtyScope,
        RegistrationRejected,
        RegistrationNull,
        RegistrationThrewFailure,
        RegistrationThrewInternalCancellation,
        HealthyControl
    }

    public enum DiscardSettlement
    {
        CompletesClean,
        CompletesStillDirty,
        ThrowsFailure,
        ThrowsInternalCancellation
    }

    public enum RecheckSettlement
    {
        ReturnsClean,
        ReturnsStillDirty,
        ThrowsAfterCancellation,
    }

    private sealed class Scenario
    {
        private Scenario(
            ServiceSetupOrchestrator orchestrator,
            CancellingStagingScope scope,
            ScriptedContributor failing,
            ScriptedContributor following,
            List<string> calls)
        {
            Orchestrator = orchestrator;
            Scope = scope;
            Failing = failing;
            Following = following;
            Calls = calls;
        }

        internal ServiceSetupOrchestrator Orchestrator { get; }

        internal CancellingStagingScope Scope { get; }

        internal ScriptedContributor Failing { get; }

        internal ScriptedContributor Following { get; }

        internal List<string> Calls { get; }

        internal static Scenario Build(
            FailureEntry entry,
            DiscardSettlement settlement,
            CancellationTokenSource? callerCancellation,
            bool cancelCallerOnDiscard,
            AsyncGate? gate = null)
        {
            var calls = new List<string>();
            var scope = new CancellingStagingScope(callerCancellation, settlement, cancelCallerOnDiscard, gate);
            var failing = new ScriptedContributor(1, "failing", calls);
            var following = new ScriptedContributor(2, "following", calls);

            switch (entry)
            {
                case FailureEntry.ValidationSideEffect:
                    failing.OnValidate = () => scope.HasPendingChanges = true;
                    break;
                case FailureEntry.ValidationThrewWithDirtyScope:
                    failing.OnValidate = () => scope.HasPendingChanges = true;
                    failing.ValidationException = new InvalidOperationException(ValidationSecret);
                    break;
                case FailureEntry.RegistrationRejected:
                    failing.RegistrationResult = ServiceSetupContributorResult.Rejected(ProductRejectedCode);
                    break;
                case FailureEntry.RegistrationNull:
                    failing.ReturnNullRegistration = true;
                    break;
                case FailureEntry.RegistrationThrewFailure:
                    failing.RegistrationException = new InvalidOperationException(RegistrationSecret);
                    break;
                case FailureEntry.RegistrationThrewInternalCancellation:
                    failing.RegistrationException = new OperationCanceledException(
                        InternalCancellationSecret,
                        new Exception(InternalCancellationInnerSecret),
                        new CancellationToken(true));
                    break;
                case FailureEntry.HealthyControl:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry));
            }

            var orchestrator = new ServiceSetupOrchestrator([failing, following], scope);
            return new Scenario(orchestrator, scope, failing, following, calls);
        }
    }

    private sealed class ScriptedContributor(int order, string name, List<string> calls)
        : IServiceSetupContributor
    {
        internal int ValidationCount { get; private set; }

        internal int RegistrationCount { get; private set; }

        internal ServiceSetupContributorResult? ValidationResult { get; set; } =
            ServiceSetupContributorResult.Success();

        internal Exception? ValidationException { get; set; }

        internal ServiceSetupContributorResult? RegistrationResult { get; set; } =
            ServiceSetupContributorResult.Success();

        internal Exception? RegistrationException { get; set; }

        internal bool ReturnNullRegistration { get; set; }

        internal Action? OnValidate { get; set; }

        internal Action? OnRegister { get; set; }

        public int Order { get; } = order;

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            calls.Add($"validate:{name}");
            ValidationCount++;
            cancellationToken.ThrowIfCancellationRequested();
            OnValidate?.Invoke();
            return ValidationException is not null
                ? ValueTask.FromException<ServiceSetupContributorResult>(ValidationException)
                : ValueTask.FromResult(ValidationResult!);
        }

        public ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default)
        {
            calls.Add($"register:{name}");
            RegistrationCount++;
            cancellationToken.ThrowIfCancellationRequested();
            OnRegister?.Invoke();
            if (RegistrationException is not null)
            {
                return ValueTask.FromException<ServiceSetupContributorResult>(RegistrationException);
            }

            return ReturnNullRegistration
                ? ValueTask.FromResult<ServiceSetupContributorResult>(null!)
                : ValueTask.FromResult(RegistrationResult!);
        }
    }

    private sealed class CancellingStagingScope(
        CancellationTokenSource? callerCancellation,
        DiscardSettlement settlement,
        bool cancelCallerOnDiscard,
        AsyncGate? gate = null) : IServiceSetupStagingScope
    {
        private readonly List<CancellationToken> discardTokens = [];
        private bool hasPendingChanges;

        internal int DiscardCount => discardTokens.Count;

        internal CancellationToken? LastDiscardToken =>
            discardTokens.Count > 0 ? discardTokens[^1] : null;

        public bool HasPendingChanges
        {
            get => hasPendingChanges;
            internal set => hasPendingChanges = value;
        }

        public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
        {
            discardTokens.Add(cancellationToken);

            // When a gate is supplied, the discard parks inside it so concurrent orchestrations
            // overlap in flight; the test releases the gate and the discard then settles.
            if (gate is not null)
            {
                return GatedDiscardAsync();
            }

            var error = SettleDiscard();
            return error is null ? ValueTask.CompletedTask : ValueTask.FromException(error);
        }

        private async ValueTask GatedDiscardAsync()
        {
            await gate!.EnterAsync().ConfigureAwait(false);
            var error = SettleDiscard();
            if (error is not null)
            {
                throw error;
            }
        }

        private Exception? SettleDiscard()
        {
            if (cancelCallerOnDiscard)
            {
                callerCancellation?.Cancel();
            }

            switch (settlement)
            {
                case DiscardSettlement.CompletesClean:
                    hasPendingChanges = false;
                    return null;
                case DiscardSettlement.CompletesStillDirty:
                    hasPendingChanges = true;
                    return null;
                case DiscardSettlement.ThrowsFailure:
                    return new InvalidOperationException(DiscardFailureSecret);
                case DiscardSettlement.ThrowsInternalCancellation:
                    return new OperationCanceledException(
                        DiscardInternalSecret,
                        new Exception(DiscardInnerSecret),
                        new CancellationToken(true));
                default:
                    throw new ArgumentOutOfRangeException(nameof(settlement));
            }
        }
    }

    /// <summary>
    /// A dedicated staging scope whose discard completes cleanly without cancelling, so the caller is
    /// cancelled only later - inside the cleanliness recheck getter that <c>CleanupFailureAsync</c>
    /// reads after the discard settles. This isolates the recheck-window cancellation that caching the
    /// cancellation state before the recheck read would otherwise miss.
    /// </summary>
    private sealed class RecheckCancellingStagingScope(
        CancellationTokenSource callerCancellation,
        RecheckSettlement recheckSettlement) : IServiceSetupStagingScope
    {
        private readonly List<CancellationToken> discardTokens = [];
        private bool hasPendingChanges;
        private bool recheckFired;

        internal int DiscardCount => discardTokens.Count;

        internal CancellationToken? LastDiscardToken =>
            discardTokens.Count > 0 ? discardTokens[^1] : null;

        internal int RecheckCancellationCount { get; private set; }

        public bool HasPendingChanges
        {
            get
            {
                // Only the cleanliness recheck - the first getter read after the single discard
                // settles - cancels the caller. Entry and validation-phase reads see a clean scope.
                if (discardTokens.Count == 1 && !recheckFired)
                {
                    recheckFired = true;
                    RecheckCancellationCount++;
                    callerCancellation.Cancel();
                    switch (recheckSettlement)
                    {
                        case RecheckSettlement.ThrowsAfterCancellation:
                            throw new InvalidOperationException(RecheckSecret);
                        case RecheckSettlement.ReturnsStillDirty:
                            return true;
                        case RecheckSettlement.ReturnsClean:
                            return false;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(recheckSettlement));
                    }
                }

                return hasPendingChanges;
            }

            internal set => hasPendingChanges = value;
        }

        public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
        {
            discardTokens.Add(cancellationToken);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A file-private two-phase gate that lets a concurrency test park concurrent orchestrations
    /// inside their failure cleanup until every expected discard has arrived, so they are genuinely
    /// in flight at the same time. The test then cancels one caller and releases all of them.
    /// </summary>
    private sealed class AsyncGate : IDisposable
    {
        private readonly SemaphoreSlim arrivals = new(0);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals that a discard has arrived, then waits until the gate is released.</summary>
        public async Task EnterAsync()
        {
            arrivals.Release();
            await release.Task.ConfigureAwait(false);
        }

        /// <summary>Waits until <paramref name="count"/> discards have arrived at the gate.</summary>
        public async Task WaitForArrivalsAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                if (!await arrivals.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                {
                    throw new TimeoutException($"Only {i} of {count} gated discards arrived.");
                }
            }
        }

        /// <summary>Releases every waiting discard. Idempotent.</summary>
        public void Release() => release.TrySetResult();

        public void Dispose() => arrivals.Dispose();
    }
}

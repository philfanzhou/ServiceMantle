using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

/// <summary>
/// Proves that every Oracle preparation path that resolves through a normal underlying return still
/// applies the ADR 0001 completion precedence: a permitted failed compensation, then caller
/// cancellation, then the overall timeout, then the triggered outcome. No barrier here throws from
/// the underlying operation, so each case reaches the completion checkpoint the way a real driver
/// that finished just as the deadline expired would.
/// </summary>
public sealed class OraclePreparationCompletionTests
{
    private const string TargetConnectionString =
        "Data Source=localhost/FREEPDB1;User Id=app_user;Password=Target-Secret-1";
    private const string AdministrativeConnectionString =
        "Data Source=localhost/FREEPDB1;User Id=system;Password=Admin-Secret-1";

    private static readonly TimeSpan UnreachableTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiringTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The four preparation paths that reach a result through a normal underlying return.</summary>
    public enum PreparationPath
    {
        ExistingUser,
        CreatedUser,
        AdoptedCreateRace,
        ConflictingUserKind
    }

    public static TheoryData<PreparationPath, DatabaseTargetPreparationOutcome?, string?>
        SettledCompletions => new()
        {
            { PreparationPath.ExistingUser, DatabaseTargetPreparationOutcome.AlreadyExists, null },
            { PreparationPath.CreatedUser, DatabaseTargetPreparationOutcome.Created, null },
            { PreparationPath.AdoptedCreateRace, DatabaseTargetPreparationOutcome.AlreadyExists, null },
            {
                PreparationPath.ConflictingUserKind,
                null,
                WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict
            }
        };

    public static TheoryData<PreparationPath> NormalReturnPaths =>
    [
        PreparationPath.ExistingUser,
        PreparationPath.CreatedUser,
        PreparationPath.AdoptedCreateRace,
        PreparationPath.ConflictingUserKind
    ];

    [Theory]
    [MemberData(nameof(SettledCompletions))]
    public async Task Settled_paths_keep_their_triggered_outcome(
        PreparationPath path,
        DatabaseTargetPreparationOutcome? outcome,
        string? errorCode)
    {
        var context = PreparationContext.For(path);

        var result = await context.Provider.PrepareAsync(
            CreateRequest(),
            UnreachableTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(context.ExpectedGrants, context.Session.GrantCount);
        Assert.Equal(0, context.Session.DropCount);
        Assert.Equal(1, context.Operations.OpenCount);
        Assert.Equal(1, context.Session.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(NormalReturnPaths))]
    public async Task Caller_cancellation_at_the_completion_checkpoint_outranks_a_normal_return(
        PreparationPath path)
    {
        using var callerSource = new CancellationTokenSource();
        var context = PreparationContext.For(path);
        context.Barrier.ThenCancelCaller = callerSource.Cancel;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Provider.PrepareAsync(
                CreateRequest(),
                UnreachableTimeout,
                callerSource.Token).AsTask());

        Assert.Equal(callerSource.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("Target-Secret-1", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Admin-Secret-1", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("localhost/FREEPDB1", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(context.ExpectedGrants, context.Session.GrantCount);
        Assert.Equal(0, context.Session.DropCount);
        Assert.Equal(1, context.Operations.OpenCount);
        Assert.Equal(1, context.Session.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(NormalReturnPaths))]
    public async Task Expired_overall_timeout_at_the_completion_checkpoint_outranks_a_normal_return(
        PreparationPath path)
    {
        var context = PreparationContext.For(path);
        context.Barrier.WaitForOverallTimeout = true;

        var result = await context.Provider.PrepareAsync(
            CreateRequest(),
            ExpiringTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        Assert.Equal(context.ExpectedGrants, context.Session.GrantCount);
        Assert.Equal(0, context.Session.DropCount);
        Assert.Equal(1, context.Operations.OpenCount);
        Assert.Equal(1, context.Session.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(NormalReturnPaths))]
    public async Task Caller_cancellation_outranks_a_simultaneously_expired_overall_timeout(
        PreparationPath path)
    {
        using var callerSource = new CancellationTokenSource();
        var context = PreparationContext.For(path);
        context.Barrier.WaitForOverallTimeout = true;
        context.Barrier.ThenCancelCaller = callerSource.Cancel;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Provider.PrepareAsync(
                CreateRequest(),
                ExpiringTimeout,
                callerSource.Token).AsTask());

        Assert.Equal(callerSource.Token, exception.CancellationToken);
        Assert.Equal(0, context.Session.DropCount);
        Assert.Equal(1, context.Session.DisposeCount);
    }

    public static TheoryData<int, string> BoundedProbeFailures => new()
    {
        { (int)OracleTargetProbeOutcome.IdentityMismatch, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
        { (int)OracleTargetProbeOutcome.UnsupportedTopology, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
        { (int)OracleTargetProbeOutcome.TopologyPermissionDenied, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { (int)OracleTargetProbeOutcome.CreateSessionDenied, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { (int)OracleTargetProbeOutcome.AccountLocked, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict },
        { (int)OracleTargetProbeOutcome.PasswordExpired, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict },
        { (int)OracleTargetProbeOutcome.InvalidCredentials, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict },
        { (int)OracleTargetProbeOutcome.ConnectionFailed, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed },
        { (int)OracleTargetProbeOutcome.ValidationFailed, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed }
    };

    [Theory]
    [MemberData(nameof(BoundedProbeFailures))]
    public async Task Bounded_probe_failures_after_creation_share_the_completion_rule(
        int probeOutcomeValue,
        string settledErrorCode)
    {
        var probeOutcome = (OracleTargetProbeOutcome)probeOutcomeValue;

        var settled = PreparationContext.For(PreparationPath.CreatedUser);
        settled.Operations.ProbeOutcome = probeOutcome;
        var settledResult = await settled.Provider.PrepareAsync(
            CreateRequest(),
            UnreachableTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(settledErrorCode, settledResult.ErrorCode);
        Assert.Equal(0, settled.Session.DropCount);

        var timedOut = PreparationContext.For(PreparationPath.CreatedUser);
        timedOut.Operations.ProbeOutcome = probeOutcome;
        timedOut.Barrier.WaitForOverallTimeout = true;
        var timedOutResult = await timedOut.Provider.PrepareAsync(
            CreateRequest(),
            ExpiringTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, timedOutResult.ErrorCode);
        Assert.Equal(0, timedOut.Session.DropCount);

        using var callerSource = new CancellationTokenSource();
        var cancelled = PreparationContext.For(PreparationPath.CreatedUser);
        cancelled.Operations.ProbeOutcome = probeOutcome;
        cancelled.Barrier.ThenCancelCaller = callerSource.Cancel;
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancelled.Provider.PrepareAsync(
                CreateRequest(),
                UnreachableTimeout,
                callerSource.Token).AsTask());

        Assert.Equal(callerSource.Token, exception.CancellationToken);
        Assert.Equal(0, cancelled.Session.DropCount);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], cancelled.Session.Actions);
    }

    [Theory]
    [InlineData((int)OracleTargetProbeOutcome.CreateSessionDenied)]
    [InlineData((int)OracleTargetProbeOutcome.InvalidCredentials)]
    public async Task Existing_user_probe_rejection_stays_conflict_until_the_completion_rule_fires(
        int probeOutcomeValue)
    {
        var probeOutcome = (OracleTargetProbeOutcome)probeOutcomeValue;

        var settled = PreparationContext.For(PreparationPath.ExistingUser);
        settled.Operations.ProbeOutcome = probeOutcome;
        var settledResult = await settled.Provider.PrepareAsync(
            CreateRequest(),
            UnreachableTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, settledResult.ErrorCode);
        Assert.Empty(settled.Session.Actions);

        var timedOut = PreparationContext.For(PreparationPath.ExistingUser);
        timedOut.Operations.ProbeOutcome = probeOutcome;
        timedOut.Barrier.WaitForOverallTimeout = true;
        var timedOutResult = await timedOut.Provider.PrepareAsync(
            CreateRequest(),
            ExpiringTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, timedOutResult.ErrorCode);
        Assert.Empty(timedOut.Session.Actions);
    }

    [Fact]
    public async Task Losing_create_race_that_re_reads_a_foreign_user_kind_shares_the_completion_rule()
    {
        var barrier = new CompletionBarrier { WaitForOverallTimeout = true };
        var session = new BarrierAdministrativeSession(
            [OracleUserMatch.Missing, OracleUserMatch.Conflicting])
        {
            CreateFailure = () => new OracleOperationException(OracleFailureKind.TargetConflict),
            BeforeLastFindUserReturn = barrier.ReachAsync
        };
        var operations = new BarrierOracleOperations();
        operations.EnqueueSession(session);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            ExpiringTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        Assert.Equal(["create:APP_USER"], session.Actions);
        Assert.Equal(0, session.DropCount);
        Assert.Equal(0, operations.ProbeCount);
    }

    /// <summary>
    /// The compensation window is still decided by the exception path, ahead of caller cancellation,
    /// and the new completion checkpoint neither widens it nor reorders it.
    /// </summary>
    [Fact]
    public async Task Failed_compensation_priority_survives_the_completion_rule()
    {
        using var callerSource = new CancellationTokenSource();
        var primary = new BarrierAdministrativeSession(OracleUserMatch.Missing)
        {
            AfterCreate = callerSource.Cancel
        };
        var compensation = new BarrierAdministrativeSession(OracleUserMatch.Exact)
        {
            DropFailure = () => new OracleOperationException(OracleFailureKind.PermissionDenied)
        };
        var operations = new BarrierOracleOperations();
        operations.EnqueueSession(primary);
        operations.EnqueueSession(compensation);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            UnreachableTimeout,
            callerSource.Token);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, result.ErrorCode);
        Assert.Equal(1, compensation.DropCount);
        Assert.Equal(0, primary.DropCount);
        Assert.Equal(0, primary.GrantCount);
    }

    [Fact]
    public async Task Cancelling_one_call_never_leaks_into_a_concurrent_call_holding_its_own_token()
    {
        using var cancelledSource = new CancellationTokenSource();
        var cancelledReachedCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var cancelledSession = new BarrierAdministrativeSession(OracleUserMatch.Missing);
        var cancelledOperations = new BarrierOracleOperations
        {
            BeforeProbeReturn = _ =>
            {
                cancelledSource.Cancel();
                cancelledReachedCompletion.SetResult();
                return ValueTask.CompletedTask;
            }
        };
        cancelledOperations.EnqueueSession(cancelledSession);

        var survivingSession = new BarrierAdministrativeSession(OracleUserMatch.Missing);
        var survivingOperations = new BarrierOracleOperations
        {
            BeforeProbeReturn = async _ =>
                await cancelledReachedCompletion.Task.ConfigureAwait(false)
        };
        survivingOperations.EnqueueSession(survivingSession);

        var surviving = new OracleDatabaseTargetPreparationProvider(survivingOperations).PrepareAsync(
                CreateRequest(),
                UnreachableTimeout,
                CancellationToken.None)
            .AsTask();
        var cancelled = new OracleDatabaseTargetPreparationProvider(cancelledOperations).PrepareAsync(
                CreateRequest(),
                UnreachableTimeout,
                cancelledSource.Token)
            .AsTask();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        var survivingResult = await surviving;

        Assert.Equal(cancelledSource.Token, exception.CancellationToken);
        Assert.True(survivingResult.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, survivingResult.Outcome);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], survivingSession.Actions);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], cancelledSession.Actions);
        Assert.Equal(0, survivingSession.DropCount);
        Assert.Equal(0, cancelledSession.DropCount);
        Assert.Equal(1, survivingSession.DisposeCount);
        Assert.Equal(1, cancelledSession.DisposeCount);
    }

    [Fact]
    public async Task Entry_priority_for_pre_cancelled_illegal_timeout_and_mismatch_is_unchanged()
    {
        using var cancelledSource = new CancellationTokenSource();
        await cancelledSource.CancelAsync();
        var operations = new BarrierOracleOperations();
        var provider = new OracleDatabaseTargetPreparationProvider(operations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.PrepareAsync(CreateRequest(), TimeSpan.Zero, cancelledSource.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.PrepareAsync(CreateRequest(), TimeSpan.Zero, CancellationToken.None).AsTask());

        var mismatch = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                TargetConnectionString),
            AdministrativeConnectionString);
        var mismatchResult = await provider.PrepareAsync(
            mismatch,
            UnreachableTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);
        Assert.Equal(0, operations.OpenCount);
        Assert.Equal(0, operations.ProbeCount);
    }

    private static DatabaseTargetPreparationRequest CreateRequest() =>
        new(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.Oracle,
                "23.26.1.0",
                TargetConnectionString),
            AdministrativeConnectionString);

    /// <summary>
    /// Wires one preparation path so that its last underlying operation - the one whose normal return
    /// produces the triggered result - reaches a shared barrier before returning.
    /// </summary>
    private sealed class PreparationContext
    {
        private PreparationContext(
            BarrierOracleOperations operations,
            BarrierAdministrativeSession session,
            CompletionBarrier barrier,
            int expectedGrants)
        {
            Operations = operations;
            Session = session;
            Barrier = barrier;
            ExpectedGrants = expectedGrants;
            Provider = new OracleDatabaseTargetPreparationProvider(operations);
        }

        internal BarrierOracleOperations Operations { get; }
        internal BarrierAdministrativeSession Session { get; }
        internal CompletionBarrier Barrier { get; }
        internal int ExpectedGrants { get; }
        internal OracleDatabaseTargetPreparationProvider Provider { get; }

        internal static PreparationContext For(PreparationPath path)
        {
            var barrier = new CompletionBarrier();
            var session = path switch
            {
                PreparationPath.ExistingUser => new BarrierAdministrativeSession(OracleUserMatch.Exact),
                PreparationPath.CreatedUser => new BarrierAdministrativeSession(OracleUserMatch.Missing),
                PreparationPath.AdoptedCreateRace => new BarrierAdministrativeSession(
                    [OracleUserMatch.Missing, OracleUserMatch.Exact])
                {
                    CreateFailure = () => new OracleOperationException(OracleFailureKind.TargetConflict)
                },
                _ => new BarrierAdministrativeSession(OracleUserMatch.Conflicting)
            };
            var operations = new BarrierOracleOperations();
            operations.EnqueueSession(session);

            // The conflicting-kind path never probes, so its last underlying return is the ALL_USERS read.
            if (path == PreparationPath.ConflictingUserKind)
            {
                session.BeforeLastFindUserReturn = barrier.ReachAsync;
            }
            else
            {
                operations.BeforeProbeReturn = barrier.ReachAsync;
            }

            return new PreparationContext(
                operations,
                session,
                barrier,
                path == PreparationPath.CreatedUser ? 1 : 0);
        }
    }

    /// <summary>
    /// Reached while the underlying operation is still going to return normally. The overall timeout
    /// is awaited through a registration on the operation token the provider supplied, so no test
    /// guesses the deadline with a fixed sleep, and the caller token is cancelled only afterwards so
    /// both signals can be observed together.
    /// </summary>
    private sealed class CompletionBarrier
    {
        internal bool WaitForOverallTimeout { get; set; }
        internal Action? ThenCancelCaller { get; set; }

        internal async ValueTask ReachAsync(CancellationToken operationToken)
        {
            if (WaitForOverallTimeout)
            {
                var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = operationToken.Register(() => expired.TrySetResult());
                await expired.Task.ConfigureAwait(false);
            }

            ThenCancelCaller?.Invoke();
        }
    }
}

/// <summary>
/// Administrative session whose last <c>ALL_USERS</c> read can reach an external barrier before it
/// returns normally, and which counts every statement it was asked to issue.
/// </summary>
internal sealed class BarrierAdministrativeSession : IOracleAdministrativeSession
{
    private readonly Queue<OracleUserMatch> matches;

    internal BarrierAdministrativeSession(OracleUserMatch match)
        : this([match])
    {
    }

    internal BarrierAdministrativeSession(IEnumerable<OracleUserMatch> matches)
    {
        this.matches = new Queue<OracleUserMatch>(matches);
    }

    internal List<string> Actions { get; } = [];
    internal Func<Exception>? CreateFailure { get; set; }
    internal Func<Exception>? DropFailure { get; set; }
    internal Action? AfterCreate { get; set; }

    /// <summary>Runs just before the final queued <c>ALL_USERS</c> read returns its normal match.</summary>
    internal Func<CancellationToken, ValueTask>? BeforeLastFindUserReturn { get; set; }

    internal int GrantCount { get; private set; }
    internal int DropCount { get; private set; }
    internal int DisposeCount { get; private set; }

    public async ValueTask<OracleUserMatch> FindUserAsync(string userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = matches.Dequeue();
        if (matches.Count == 0 && BeforeLastFindUserReturn is not null)
        {
            await BeforeLastFindUserReturn(cancellationToken).ConfigureAwait(false);
        }

        return match;
    }

    public ValueTask CreateUserAsync(string userName, string password, CancellationToken cancellationToken)
    {
        Actions.Add($"create:{userName}");
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateFailure is not null)
        {
            return ValueTask.FromException(CreateFailure());
        }

        AfterCreate?.Invoke();
        return ValueTask.CompletedTask;
    }

    public ValueTask GrantCreateSessionAsync(string userName, CancellationToken cancellationToken)
    {
        Actions.Add($"grant:{userName}");
        GrantCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DropUserAsync(string userName, CancellationToken cancellationToken)
    {
        Actions.Add($"drop:{userName}");
        DropCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return DropFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DropFailure());
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Database operations whose target probe can reach an external barrier before returning its normal
/// outcome, so the completion checkpoint is exercised without any underlying exception.
/// </summary>
internal sealed class BarrierOracleOperations : IOracleDatabaseOperations
{
    private readonly Queue<IOracleAdministrativeSession> sessions = new();

    internal OracleTargetProbeOutcome ProbeOutcome { get; set; } = OracleTargetProbeOutcome.Success;

    /// <summary>Runs just before the target probe returns its normal outcome.</summary>
    internal Func<CancellationToken, ValueTask>? BeforeProbeReturn { get; set; }

    internal int ProbeCount { get; private set; }
    internal int OpenCount { get; private set; }

    internal void EnqueueSession(IOracleAdministrativeSession session) => sessions.Enqueue(session);

    public async ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        ProbeCount++;
        if (BeforeProbeReturn is not null)
        {
            await BeforeProbeReturn(cancellationToken).ConfigureAwait(false);
        }

        return ProbeOutcome;
    }

    public ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        OpenCount++;
        if (sessions.Count == 0)
        {
            throw new OracleOperationException(OracleFailureKind.Unexpected);
        }

        return ValueTask.FromResult(sessions.Dequeue());
    }
}

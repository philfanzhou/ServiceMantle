using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Health;

/// <summary>
/// Covers the shared final readiness decision: it keeps the exact snapshot it was evaluated from,
/// cannot report Ready for a snapshot the base matrix rejects, and projects only safe values.
/// </summary>
public sealed class ServiceReadinessDecisionTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    public static TheoryData<
        ServiceStartupPhase,
        ServiceMigrationReadinessState,
        ServiceDatabaseReadinessState> Matrix
    {
        get
        {
            var data = new TheoryData<
                ServiceStartupPhase,
                ServiceMigrationReadinessState,
                ServiceDatabaseReadinessState>();
            foreach (var phase in Enum.GetValues<ServiceStartupPhase>())
            {
                foreach (var migration in Enum.GetValues<ServiceMigrationReadinessState>())
                {
                    foreach (var database in Enum.GetValues<ServiceDatabaseReadinessState>())
                    {
                        data.Add(phase, migration, database);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Ready_accepts_exactly_the_base_ready_combination(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var snapshot = new ServiceHealthSnapshot(phase, migration, database);
        var baseReady = ServiceHealthEvaluator.Evaluate(snapshot).IsReady;

        if (baseReady)
        {
            var decision = ServiceReadinessDecision.Ready(snapshot);
            Assert.True(decision.IsReady);
            Assert.Same(snapshot, decision.Snapshot);
            Assert.Null(decision.ErrorCode);
            return;
        }

        var error = Assert.Throws<ArgumentException>(() => ServiceReadinessDecision.Ready(snapshot));
        Assert.Equal("snapshot", error.ParamName);
    }

    [Fact]
    public void Ready_never_carries_a_rejection_code_even_when_the_snapshot_has_one()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable,
            "migration.replayed");

        var decision = ServiceReadinessDecision.Ready(snapshot);

        Assert.True(decision.IsReady);
        Assert.Null(decision.ErrorCode);
        Assert.Same(snapshot, decision.Snapshot);
        Assert.Equal("migration.replayed", decision.Snapshot!.ErrorCode);
    }

    [Fact]
    public void NotReady_keeps_the_evaluated_snapshot_and_the_safe_code()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Running,
            ServiceDatabaseReadinessState.Unreachable,
            "database.unreachable");

        var withCode = ServiceReadinessDecision.NotReady(snapshot, "health.contributor_failed");
        var withoutCode = ServiceReadinessDecision.NotReady(snapshot);

        Assert.False(withCode.IsReady);
        Assert.Same(snapshot, withCode.Snapshot);
        Assert.Equal("health.contributor_failed", withCode.ErrorCode);
        Assert.False(withoutCode.IsReady);
        Assert.Same(snapshot, withoutCode.Snapshot);
        Assert.Null(withoutCode.ErrorCode);
    }

    [Fact]
    public void Unavailable_has_no_snapshot_and_requires_a_code()
    {
        var decision = ServiceReadinessDecision.Unavailable(
            WellKnownServiceReadinessDecisionErrorCodes.ProbeFailed);

        Assert.False(decision.IsReady);
        Assert.Null(decision.Snapshot);
        Assert.Equal("health.probe_failed", decision.ErrorCode);
        Assert.Throws<ArgumentNullException>(() => ServiceReadinessDecision.Unavailable(null!));
    }

    [Fact]
    public void Decision_factories_reject_missing_snapshots()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceReadinessDecision.Ready(null!));
        Assert.Throws<ArgumentNullException>(() => ServiceReadinessDecision.NotReady(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".leading")]
    [InlineData("has space")]
    [InlineData("password=secret;")]
    public void Decision_error_codes_stay_inside_the_safe_grammar(string errorCode)
    {
        Assert.Throws<ArgumentException>(() =>
            ServiceReadinessDecision.NotReady(ReadySnapshot, errorCode));
        Assert.Throws<ArgumentException>(() => ServiceReadinessDecision.Unavailable(errorCode));
    }

    [Fact]
    public void Decision_is_immutable_and_projects_only_finite_state()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.Failed,
            ServiceDatabaseReadinessState.Unreachable,
            "migration.failed");
        var decision = ServiceReadinessDecision.NotReady(snapshot, "migration.failed");

        Assert.All(
            typeof(ServiceReadinessDecision).GetProperties(),
            property => Assert.Null(property.SetMethod));
        Assert.Contains("IsReady=False", decision.ToString(), StringComparison.Ordinal);
        Assert.Contains("migration.failed", decision.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "<none>",
            ServiceReadinessDecision.Unavailable("health.probe_timeout").ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("connection", decision.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Well_known_decision_codes_match_the_published_contract()
    {
        Assert.Equal("health.probe_timeout", WellKnownServiceReadinessDecisionErrorCodes.ProbeTimeout);
        Assert.Equal("health.probe_failed", WellKnownServiceReadinessDecisionErrorCodes.ProbeFailed);
    }

    [Fact]
    public void Core_decision_contract_does_not_reference_AspNetCore()
    {
        Assert.DoesNotContain(
            typeof(IServiceReadinessDecisionSource).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
        Assert.Equal(
            typeof(ServiceHealthSnapshot).Assembly,
            typeof(IServiceReadinessDecisionSource).Assembly);
    }

    [Fact]
    public async Task Any_optional_package_can_consume_the_source_without_AspNetCore()
    {
        IServiceReadinessDecisionSource source = new FixedDecisionSource(
            ServiceReadinessDecision.Ready(ReadySnapshot));

        var decision = await source.GetDecisionAsync(TestContext.Current.CancellationToken);

        Assert.True(decision.IsReady);
        Assert.Same(ReadySnapshot, decision.Snapshot);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_the_original_token()
    {
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        IServiceReadinessDecisionSource source = new FixedDecisionSource(
            ServiceReadinessDecision.Ready(ReadySnapshot));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetDecisionAsync(abort.Token));

        Assert.Equal(abort.Token, error.CancellationToken);
    }

    private sealed class FixedDecisionSource(ServiceReadinessDecision decision)
        : IServiceReadinessDecisionSource
    {
        public ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }
}

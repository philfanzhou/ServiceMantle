using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public sealed class StartupDatabasePreparationTests
{
    private const string Secret = "standalone-private-value";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ServiceId Service = ServiceId.Parse("standalone-prepare");

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var file in new[] { true, false })
        foreach (var scenario in new[] { "disabled", "deployment-denied", "no-provider", "connectable", "created", "already-exists", "creation-denied", "maintenance-missing", "unreachable", "observe-error", "prepare-error", "reobserve-error", "observe-oce", "prepare-oce", "reobserve-oce", "prepare-failure", "prepare-timeout", "reobserve-unreachable" })
            yield return [file, scenario];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Standalone_and_full_run_share_results_order_counts_and_timeout(bool file, string scenario)
    {
        var standalone = new Harness(file, scenario, rejectScope: true);
        var full = new Harness(file, scenario);
        var prepared = await standalone.Gate.PrepareAsync(standalone.Options, Token);
        var run = await full.Gate.RunAsync(full.Options, full.Receipt, Service, Token);
        var expected = Expected(file, scenario);
        Assert.Equal(expected.Code is null, prepared.Succeeded);
        Assert.Equal(expected.Code, prepared.ErrorCode);
        Assert.Equal(scenario == "disabled", prepared.Skipped);
        Assert.Equal(prepared.Succeeded, run.Succeeded);
        Assert.Equal(prepared.ErrorCode, run.ErrorCode);
        Assert.Equal(expected.Observations, standalone.Provider.Observations);
        Assert.Equal(expected.Preparations, standalone.Provider.Preparations);
        Assert.Equal(standalone.Provider.Observations, full.Provider.Observations);
        Assert.Equal(standalone.Provider.Preparations, full.Provider.Preparations);
        Assert.Equal(0, standalone.Scope.Creations);
        Assert.Equal(0, standalone.Executor.Inspections);
        Assert.Equal(ServiceMigrationReadinessState.NotStarted, standalone.Receipt.State);
        Assert.Equal(prepared.Succeeded ? ServiceMigrationReadinessState.Succeeded : ServiceMigrationReadinessState.Failed, full.Receipt.State);
        if (expected.Preparations != 0)
        {
            Assert.Equal(standalone.Options.PreparationTimeout, standalone.Provider.Timeout);
            Assert.Same(standalone.Options.Database, standalone.Provider.Request!.Target);
            Assert.Equal(file ? null : "maintenance;Password=" + Secret, standalone.Provider.Request.AdministrativeConnectionString);
        }
        Assert.DoesNotContain(Secret, prepared + JsonSerializer.Serialize(prepared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepared_then_explicit_disabled_run_does_not_repeat_observation_or_creation(bool file)
    {
        var harness = new Harness(file, "created");
        var prepared = await harness.Gate.PrepareAsync(harness.Options, Token);
        Assert.True(prepared.Succeeded);
        Assert.False(prepared.Skipped);
        Assert.Equal(0, harness.Scope.Creations);
        var skipped = await harness.Gate.PrepareAsync(harness.DisabledOptions, Token);
        Assert.True(skipped.Succeeded);
        Assert.True(skipped.Skipped);
        var run = await harness.Gate.RunAsync(harness.DisabledOptions, harness.Receipt, Service, Token);
        Assert.True(run.Succeeded);
        Assert.Equal(2, harness.Provider.Observations);
        Assert.Equal(1, harness.Provider.Preparations);
        Assert.Equal(1, harness.Scope.Creations);
        Assert.Equal(1, harness.Executor.Inspections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Standalone_never_changes_any_receipt_state_and_does_not_cache_preparation(int state)
    {
        var harness = new Harness(true, "connectable", rejectScope: true);
        if (state > 0) harness.Receipt.TryMarkRunning();
        if (state == 2) harness.Receipt.TryCompleteSucceeded();
        if (state == 3) harness.Receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed);
        var previous = (harness.Receipt.State, harness.Receipt.ErrorCode);
        Assert.True((await harness.Gate.PrepareAsync(harness.Options, Token)).Succeeded);
        Assert.True((await harness.Gate.PrepareAsync(harness.Options, Token)).Succeeded);
        Assert.Equal(previous, (harness.Receipt.State, harness.Receipt.ErrorCode));
        Assert.Equal(2, harness.Provider.Observations);
        Assert.Equal(0, harness.Scope.Creations);
    }

    public static IEnumerable<object[]> CancellationScenarios()
    {
        foreach (var full in new[] { false, true })
        foreach (var stage in new[] { "entry", "first", "prepare", "second" })
        foreach (var failure in new[] { "normal-success", "normal-failure", "exception", "cleanup" })
            yield return [full, stage, failure];
    }

    [Theory]
    [MemberData(nameof(CancellationScenarios))]
    public async Task Caller_cancellation_wins_at_every_shared_completion_boundary(bool full, string stage, string failure)
    {
        using var cts = new CancellationTokenSource();
        var harness = new Harness(false, "created", rejectScope: true);
        harness.Provider.Cancel = cts;
        harness.Provider.CancelStage = stage;
        harness.Provider.FailureMode = failure;
        if (stage == "entry") cts.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (full) await harness.Gate.RunAsync(harness.Options, harness.Receipt, Service, cts.Token);
            else await harness.Gate.PrepareAsync(harness.Options, cts.Token);
        });
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, harness.Scope.Creations);
        Assert.Equal(full ? ServiceMigrationReadinessState.Running : ServiceMigrationReadinessState.NotStarted, harness.Receipt.State);
        Assert.Equal(stage == "entry" ? 0 : stage == "second" ? 2 : 1, harness.Provider.Observations);
        Assert.Equal(stage is "prepare" or "second" ? 1 : 0, harness.Provider.Preparations);
        if (failure == "cleanup" && stage != "entry") Assert.True(harness.Provider.Cleaned);
    }

    [Fact]
    public void Null_options_are_rejected_without_scope_creation()
    {
        var harness = new Harness(true, "created", rejectScope: true);
        Assert.Throws<ArgumentNullException>(() => harness.Gate.PrepareAsync(null!, Token));
        Assert.Equal(0, harness.Scope.Creations);
    }

    private static (string? Code, int Observations, int Preparations) Expected(bool file, string scenario) => scenario switch
    {
        "disabled" => (null, 0, 0),
        "deployment-denied" => (WellKnownMigrationErrorCodes.LockNotSupported, 0, 0),
        "no-provider" => (WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported, 0, 0),
        "connectable" => (null, 1, 0),
        "created" or "already-exists" => (null, 2, 1),
        "creation-denied" => (WellKnownDatabaseTargetPreparationErrorCodes.CreationNotAllowed, 1, 0),
        "maintenance-missing" => file ? (null, 2, 1) : (WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, 1, 0),
        "unreachable" => (WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed, 1, 0),
        "observe-error" or "observe-oce" => (WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, 1, 0),
        "prepare-error" or "prepare-oce" => (WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, 1, 1),
        "prepare-failure" => (WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, 1, 1),
        "prepare-timeout" => (WellKnownDatabaseTargetPreparationErrorCodes.Timeout, 1, 1),
        _ => (WellKnownDatabaseTargetPreparationErrorCodes.NotConnectableAfterPreparation, 2, 1)
    };

    private sealed class Harness
    {
        public StartupDatabaseGate Gate { get; }
        public StartupDatabaseReceipt Receipt { get; } = new();
        public StartupDatabaseGateOptions Options { get; }
        public StartupDatabaseGateOptions DisabledOptions => new(Options.Database, Options.DeploymentMode, Options.LockWaitBudget);
        public Script Provider { get; }
        public Executor Executor { get; } = new();
        public ScopeFactory Scope { get; }
        public Harness(bool file, string scenario, bool rejectScope = false)
        {
            Provider = new(file, scenario);
            Scope = new(Executor, rejectScope);
            Gate = new(new([new Declaration(Provider.ProviderId)], DatabaseProviderIdResolver.Empty),
                new(scenario == "no-provider" ? [] : [Provider], DatabaseProviderIdResolver.Empty),
                new([], DatabaseProviderIdResolver.Empty), Scope);
            Options = new(new(Provider.ProviderId, file ? null : "16", "target;Password=" + Secret),
                scenario == "deployment-denied" ? DatabaseDeploymentMode.MultiInstance : DatabaseDeploymentMode.SingleInstance,
                TimeSpan.FromSeconds(5), scenario != "disabled", scenario != "creation-denied" && scenario != "disabled",
                file || scenario == "maintenance-missing" ? null : "maintenance;Password=" + Secret,
                preparationTimeout: TimeSpan.FromSeconds(7));
        }
    }

    private sealed class Script(bool file, string scenario) : IDatabaseTargetPreparationProvider
    {
        public string ProviderId => file ? WellKnownDatabaseProviderIds.Sqlite : WellKnownDatabaseProviderIds.PostgreSql;
        public BootstrapDatabaseTargetKind TargetKind => file ? BootstrapDatabaseTargetKind.File : BootstrapDatabaseTargetKind.ServerDatabase;
        public int Observations, Preparations;
        public TimeSpan Timeout;
        public DatabaseTargetPreparationRequest? Request;
        public CancellationTokenSource? Cancel;
        public string? CancelStage, FailureMode;
        public bool Cleaned;

        private async ValueTask CompletionAsync(string stage)
        {
            if (stage != CancelStage) return;
            if (FailureMode == "cleanup")
            {
                try { await Task.Yield(); }
                finally { Cleaned = true; Cancel!.Cancel(); }
            }
            else Cancel!.Cancel();
            if (FailureMode == "exception") throw new InvalidOperationException(Secret);
        }
        public async ValueTask<DatabaseTargetObservation> ObserveAsync(BootstrapDatabaseConfiguration target, CancellationToken token)
        {
            Observations++;
            var stage = Observations == 1 ? "first" : "second";
            await CompletionAsync(stage);
            if (scenario == "observe-error" || (Observations == 2 && scenario == "reobserve-error")) throw new InvalidOperationException(Secret);
            if (scenario == "observe-oce" || (Observations == 2 && scenario == "reobserve-oce")) throw new OperationCanceledException(Secret);
            if ((stage == CancelStage && FailureMode == "normal-failure") || scenario == "unreachable" || (Observations == 2 && scenario == "reobserve-unreachable"))
                return DatabaseTargetObservation.TargetUnreachable(WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed);
            return scenario == "connectable" || Observations == 2 || (stage == CancelStage && FailureMode == "normal-success")
                ? DatabaseTargetObservation.TargetConnectable() : DatabaseTargetObservation.TargetMissing();
        }
        public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(DatabaseTargetPreparationRequest request, TimeSpan timeout, CancellationToken token)
        {
            Preparations++; Request = request; Timeout = timeout;
            await CompletionAsync("prepare");
            if (scenario == "prepare-error") throw new InvalidOperationException(Secret);
            if (scenario == "prepare-oce") throw new OperationCanceledException(Secret);
            if ((CancelStage == "prepare" && FailureMode == "normal-failure") || scenario == "prepare-failure")
                return DatabaseTargetPreparationResult.Failure(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied);
            if (scenario == "prepare-timeout") return DatabaseTargetPreparationResult.Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
            return DatabaseTargetPreparationResult.Success(scenario == "already-exists" ? DatabaseTargetPreparationOutcome.AlreadyExists : DatabaseTargetPreparationOutcome.Created);
        }
    }

    private sealed class Declaration(string provider) : IDatabaseDeploymentCapabilityProvider
    {
        private readonly string identity = Guid.NewGuid().ToString();
        public DatabaseDeploymentCapability Capability { get; } = new(provider, DatabaseDeploymentSupport.SingleInstanceOnly);
        public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken token) => ValueTask.FromResult(identity);
    }
    private sealed class Executor : IDatabaseMigrationExecutor
    {
        public int Inspections;
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default) { Inspections++; return ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible); }
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    private sealed class ScopeFactory(Executor executor, bool reject) : IServiceScopeFactory
    {
        public int Creations;
        public IServiceScope CreateScope() { Creations++; if (reject) throw new InvalidOperationException("Preparation must not create a migration scope."); return new Scope(executor); }
        private sealed class Scope(Executor executor) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type type) => type == typeof(IDatabaseMigrationExecutor) ? executor : null;
            public void Dispose() { }
        }
    }
}

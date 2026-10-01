using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public sealed class StartupDatabaseGateTests
{
    private static readonly ServiceId Service = ServiceId.Parse("gate-test");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private const string Secret = "gate-secret-value";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------- receipt lifecycle ----------

    [Fact]
    public async Task Successful_gate_records_succeeded_receipt()
    {
        var harness = GateHarness.Create(DatabaseDeploymentMode.SingleInstance);
        var result = await harness.RunAsync(Token);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        Assert.Null(harness.Receipt.ErrorCode);
    }

    [Fact]
    public async Task Failing_migration_stage_records_failed_receipt_with_safe_code()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            executor: new FakeMigrationExecutor(
                sequentialStates: [MigrationObservationState.InspectionFailed]));
        var result = await harness.RunAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.InspectionFailed, result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(WellKnownMigrationErrorCodes.InspectionFailed, harness.Receipt.ErrorCode);
        AssertSafe(result);
    }

    [Fact]
    public async Task Second_gate_run_on_the_same_receipt_throws_instead_of_re_running_stages()
    {
        var harness = GateHarness.Create(DatabaseDeploymentMode.SingleInstance);
        await harness.RunAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(Token).AsTask());
        Assert.Equal(2, harness.Executor!.InspectCallCount);
    }

    [Fact]
    public async Task Receipt_terminal_state_is_never_overwritten()
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        Assert.True(receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed));
        Assert.False(receipt.TryCompleteSucceeded());
        Assert.False(receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed));
        Assert.Equal(ServiceMigrationReadinessState.Failed, receipt.State);
        Assert.Equal(WellKnownMigrationErrorCodes.ExecutionFailed, receipt.ErrorCode);
    }

    [Fact]
    public async Task Concurrent_reads_never_observe_an_undefined_or_backward_state()
    {
        // The one-way state machine under concurrent reads: every observed value is defined and
        // no reader ever observes a state that moves backwards. The test deliberately avoids
        // handshakes on observing specific values - which make it scheduling-fragile under a
        // fully loaded test run - and asserts the monotonicity invariant instead.
        var receipt = new StartupDatabaseReceipt();
        const int readers = 3;
        const int readsPerReader = 200_000;
        var readings = new int[readers][];
        for (var i = 0; i < readers; i++)
        {
            readings[i] = new int[readsPerReader];
        }

        var tasks = Enumerable.Range(0, readers).Select(readerIndex => Task.Run(() =>
        {
            var buffer = readings[readerIndex];
            for (var i = 0; i < readsPerReader; i++)
            {
                buffer[i] = (int)receipt.State;
            }
        })).ToArray();

        receipt.TryMarkRunning();
        receipt.TryCompleteSucceeded();
        await Task.WhenAll(tasks);

        Assert.Equal(ServiceMigrationReadinessState.Succeeded, receipt.State);
        foreach (var buffer in readings)
        {
            Assert.All(buffer, value => Assert.InRange(value, 0, 3));
            for (var i = 1; i < buffer.Length; i++)
            {
                Assert.True(buffer[i] >= buffer[i - 1], "A concurrent read observed a backward state.");
            }
        }
    }

    // ---------- deployment validation runs first, from declarations only ----------

    [Fact]
    public async Task Unspecified_deployment_mode_is_refused_before_any_target_side_effect()
    {
        // An unspecified mode is already refused when the gate inputs are constructed, so it can
        // never reach deployment validation, let alone a target observation.
        var harness = GateHarness.Create(DatabaseDeploymentMode.SingleInstance);
        Assert.Throws<ArgumentException>(() => harness.OptionsWith(DatabaseDeploymentMode.Unspecified));
        Assert.Equal(0, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
        Assert.Equal(ServiceMigrationReadinessState.NotStarted, harness.Receipt.State);
    }

    [Fact]
    public async Task MultiInstance_against_a_single_instance_capability_fails_closed_before_io()
    {
        // The capability declares single-instance support only, so a multi-instance declaration is
        // rejected from declarations alone, before any target observation. This is the shape a
        // SQLite target takes when MultiInstance is declared against it.
        var harness = GateHarness.Create(DatabaseDeploymentMode.SingleInstance);
        var result = await harness.Gate.RunAsync(
            harness.OptionsWith(DatabaseDeploymentMode.MultiInstance), harness.Receipt, Service, Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(0, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Preparation.PrepareCallCount);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
        Assert.Equal(0, harness.Capability.IdentityCalls);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, harness.Receipt.ErrorCode);
        AssertSafe(result);
    }

    [Fact]
    public async Task Unregistered_deployment_capability_fails_closed_without_io()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance, withCapability: false);
        var result = await harness.RunAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(0, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
    }

    // ---------- target preparation is an explicit switch ----------

    [Fact]
    public async Task Disabled_target_preparation_never_calls_a_preparation_provider()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance, enableTargetPreparation: false);
        var result = await harness.RunAsync(Token);

        Assert.True(result.Succeeded);
        Assert.Equal(0, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Preparation.PrepareCallCount);
    }

    [Fact]
    public async Task Enabled_preparation_without_a_registered_provider_fails_closed()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            withPreparationProvider: false);
        var result = await harness.RunAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
            result.ErrorCode);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
    }

    // ---------- the preparation paths ----------

    public static IEnumerable<object[]> PreparationPaths()
    {
        yield return new object[]
        {
            new PreparationScript(
                FirstObservation: DatabaseTargetObservation.TargetConnectable(),
                AllowCreation: true,
                PrepareOutcome: DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.Created),
                SecondObservation: DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: true,
                ExpectedErrorCode: null,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 0),
        };
        yield return new object[]
        {
            // Missing target, creation permitted, connectable after preparation.
            new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: true,
                ExpectedErrorCode: null,
                ExpectedObserveCalls: 2,
                ExpectedPrepareCalls: 1),
        };
        yield return new object[]
        {
            // Missing target, creation not permitted.
            new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: false,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.CreationNotAllowed,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 0),
        };
        yield return new object[]
        {
            // Unreachable server.
            new PreparationScript(
                DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 0),
        };
        yield return new object[]
        {
            // Authentication failure on the target.
            new PreparationScript(
                DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 0),
        };
        yield return new object[]
        {
            // Prepared successfully but still not connectable.
            new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes
                    .NotConnectableAfterPreparation,
                ExpectedObserveCalls: 2,
                ExpectedPrepareCalls: 1),
        };
        yield return new object[]
        {
            // Preparation itself failed.
            new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 1),
        };
    }

    [Theory]
    [MemberData(nameof(PreparationPaths))]
    public async Task Preparation_paths_report_deterministic_codes(PreparationScript script)
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            allowTargetCreation: script.AllowCreation,
            script: script);
        var result = await harness.RunAsync(Token);

        Assert.Equal(script.ExpectedSuccess, result.Succeeded);
        Assert.Equal(script.ExpectedErrorCode, result.ErrorCode);
        Assert.Equal(script.ExpectedObserveCalls, harness.Preparation.ObserveCallCount);
        Assert.Equal(script.ExpectedPrepareCalls, harness.Preparation.PrepareCallCount);
        Assert.Equal(script.ExpectedSuccess ? 2 : 0, harness.Executor!.InspectCallCount);
        Assert.Equal(
            script.ExpectedSuccess
                ? ServiceMigrationReadinessState.Succeeded
                : ServiceMigrationReadinessState.Failed,
            harness.Receipt.State);
        AssertSafe(result);
    }

    [Fact]
    public async Task Creation_permitted_without_a_maintenance_connection_fails_closed()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            allowTargetCreation: true,
            maintenanceConnectionString: null,
            script: new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
                ExpectedObserveCalls: 1,
                ExpectedPrepareCalls: 0));
        var result = await harness.RunAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Equal(1, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Preparation.PrepareCallCount);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
    }

    [Fact]
    public async Task Observation_failure_surfaces_as_a_safe_preparation_failure()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            allowTargetCreation: true,
            executor: new FakeMigrationExecutor(
                sequentialStates:
                [
                    MigrationObservationState.Empty,
                    MigrationObservationState.CurrentVersionCompatible,
                ]),
            preparationObserveException: new IOException("driver failure with " + Secret));
        var result = await harness.RunAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, result.ErrorCode);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        AssertSafe(result);
    }

    // ---------- migration stage ----------

    [Fact]
    public async Task MultiInstance_with_a_real_lock_uses_the_registered_lock_provider()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.MultiInstance,
            capabilitySupport: DatabaseDeploymentSupport.SingleAndMultiInstance,
            lockProvider: new FakeMigrationLockProvider("CustomDb"));
        var result = await harness.RunAsync(Token);

        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
    }

    [Fact]
    public async Task Missing_executor_registration_fails_with_execution_failed_receipt()
    {
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance, registerExecutor: false);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(Token).AsTask());

        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(WellKnownMigrationErrorCodes.ExecutionFailed, harness.Receipt.ErrorCode);
        AssertSafe(exception);
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task Pre_cancelled_token_fails_before_any_stage()
    {
        var harness = GateHarness.Create(DatabaseDeploymentMode.SingleInstance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(cts.Token).AsTask());

        Assert.Equal(ServiceMigrationReadinessState.Running, harness.Receipt.State);
        Assert.Equal(0, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
    }

    [Fact]
    public async Task Cancellation_during_target_observation_never_reaches_migration()
    {
        using var cts = new CancellationTokenSource();
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            allowTargetCreation: true,
            script: new PreparationScript(
                DatabaseTargetObservation.TargetMissing(),
                AllowCreation: true,
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created),
                DatabaseTargetObservation.TargetConnectable(),
                ExpectedSuccess: true,
                ExpectedErrorCode: null,
                ExpectedObserveCalls: 2,
                ExpectedPrepareCalls: 1),
            cancelOnFirstObservation: cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(cts.Token).AsTask());

        Assert.Equal(1, harness.Preparation.ObserveCallCount);
        Assert.Equal(0, harness.Preparation.PrepareCallCount);
        Assert.Equal(0, harness.Executor!.InspectCallCount);
        Assert.NotEqual(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        Assert.Null(harness.Receipt.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_during_migration_orchestration_never_records_success()
    {
        using var cts = new CancellationTokenSource();
        var harness = GateHarness.Create(
            DatabaseDeploymentMode.SingleInstance,
            executor: new FakeMigrationExecutor(
                sequentialStates:
                [
                    MigrationObservationState.Empty,
                    MigrationObservationState.CurrentVersionCompatible,
                ],
                inspectDelay: (_, token) =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(cts.Token).AsTask());

        // Cancelled at the first inspection inside the orchestration: the executor never runs
        // and no success is recorded.
        Assert.Equal(0, harness.Executor!.ExecuteCallCount);
        Assert.Equal(1, harness.Executor!.InspectCallCount);
        Assert.NotEqual(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        Assert.Null(harness.Receipt.ErrorCode);
    }

    private static void AssertSafe(object value)
    {
        var text = value + (value is Exception ? "" : JsonSerializer.Serialize(value));
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("gate-secret-value", text, StringComparison.Ordinal);
    }

    public sealed record PreparationScript(
        DatabaseTargetObservation FirstObservation,
        bool AllowCreation,
        DatabaseTargetPreparationResult? PrepareOutcome,
        DatabaseTargetObservation? SecondObservation,
        bool ExpectedSuccess,
        string? ExpectedErrorCode,
        int ExpectedObserveCalls,
        int ExpectedPrepareCalls);

    private sealed class GateCapabilityProvider(
        string provider = "CustomDb",
        DatabaseDeploymentSupport support = DatabaseDeploymentSupport.SingleInstanceOnly)
        : IDatabaseDeploymentCapabilityProvider
    {
        private readonly string identity = Guid.NewGuid().ToString();
        public DatabaseDeploymentCapability Capability { get; } = new(provider, support);
        public int IdentityCalls;

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref IdentityCalls);
            return ValueTask.FromResult(identity);
        }
    }

    private sealed class ScriptedPreparationProvider(
        PreparationScript? script,
        Exception? observeException = null,
        CancellationTokenSource? cancelOnFirstObservation = null)
        : IDatabaseTargetPreparationProvider
    {
        public string ProviderId => "CustomDb";
        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;
        public int ObserveCallCount { get; private set; }
        public int PrepareCallCount { get; private set; }

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            ObserveCallCount++;
            if (ObserveCallCount == 1)
            {
                cancelOnFirstObservation?.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (observeException is not null)
            {
                throw observeException;
            }

            var observation = ObserveCallCount == 1
                ? script?.FirstObservation ?? DatabaseTargetObservation.TargetConnectable()
                : script?.SecondObservation ?? DatabaseTargetObservation.TargetConnectable();
            return ValueTask.FromResult(observation);
        }

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                script?.PrepareOutcome ??
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created));
        }
    }

    private sealed class ProviderStub(
        IDatabaseMigrationExecutor? executor,
        bool registerExecutor = true) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDatabaseMigrationExecutor) && registerExecutor ? executor : null;
    }

    private sealed class ScopeFactoryStub(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ScopeStub(provider);

        private sealed class ScopeStub(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
    }

    private sealed class GateHarness
    {
        public StartupDatabaseGate Gate { get; }
        public StartupDatabaseReceipt Receipt { get; } = new();
        public StartupDatabaseGateOptions Options { get; }
        public FakeMigrationExecutor? Executor { get; }
        public ScriptedPreparationProvider Preparation { get; }
        public GateCapabilityProvider Capability { get; }

        private GateHarness(
            StartupDatabaseGate gate,
            StartupDatabaseGateOptions options,
            FakeMigrationExecutor? executor,
            ScriptedPreparationProvider preparation,
            GateCapabilityProvider capability)
        {
            Gate = gate;
            Options = options;
            Executor = executor;
            Preparation = preparation;
            Capability = capability;
        }

        public static GateHarness Create(
            DatabaseDeploymentMode mode,
            bool enableTargetPreparation = false,
            bool allowTargetCreation = false,
            string? maintenanceConnectionString = "maintenance;Password=" + Secret,
            PreparationScript? script = null,
            bool withPreparationProvider = true,
            bool withCapability = true,
            DatabaseDeploymentSupport capabilitySupport = DatabaseDeploymentSupport.SingleInstanceOnly,
            FakeMigrationExecutor? executor = null,
            FakeMigrationLockProvider? lockProvider = null,
            Exception? preparationObserveException = null,
            CancellationTokenSource? cancelOnFirstObservation = null,
            bool registerExecutor = true)
        {
            executor ??= new FakeMigrationExecutor(
                sequentialStates:
                [
                    MigrationObservationState.Empty,
                    MigrationObservationState.CurrentVersionCompatible,
                ]);
            var preparation = new ScriptedPreparationProvider(
                script, preparationObserveException, cancelOnFirstObservation);
            var capability = new GateCapabilityProvider("CustomDb", capabilitySupport);
            var target = new BootstrapDatabaseConfiguration(
                "CustomDb", "1", "target=one;Password=" + Secret);
            var options = new StartupDatabaseGateOptions(
                target,
                mode,
                Budget,
                enableTargetPreparation,
                allowTargetCreation,
                maintenanceConnectionString);

            IDatabaseMigrationLockProvider[] providers = lockProvider is null ? [] : [lockProvider];
            var gate = new StartupDatabaseGate(
                new DatabaseDeploymentCapabilityRegistry(
                    withCapability ? [capability] : [],
                    DatabaseProviderIdResolver.Empty),
                new DatabaseTargetPreparationProviderRegistry(
                    withPreparationProvider ? [preparation] : [],
                    DatabaseProviderIdResolver.Empty),
                new DatabaseMigrationLockProviderRegistry(
                    providers, DatabaseProviderIdResolver.Empty),
                new ScopeFactoryStub(new ProviderStub(executor, registerExecutor)));

            return new GateHarness(gate, options, executor, preparation, capability);
        }

        public StartupDatabaseGateOptions OptionsWith(DatabaseDeploymentMode mode) =>
            new(
                Options.Database,
                mode,
                Options.LockWaitBudget,
                Options.EnableTargetPreparation,
                Options.AllowTargetCreation,
                Options.MaintenanceConnectionString);

        public ValueTask<StartupDatabaseGateResult> RunAsync(
            CancellationToken cancellationToken = default) =>
            Gate.RunAsync(Options, Receipt, Service, cancellationToken);
    }
}

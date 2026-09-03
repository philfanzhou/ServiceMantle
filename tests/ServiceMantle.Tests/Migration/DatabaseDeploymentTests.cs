using System.Text.Json;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public sealed class DatabaseDeploymentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ServiceId Service = ServiceId.Parse("deployment-test");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Secret = "private-path;Password=deployment-secret";
    private static BootstrapDatabaseConfiguration Target(string provider = "CustomDb", string password = "first") =>
        new(provider, "1", "target=one;Password=" + password);

    public static IEnumerable<object[]> Modes =>
        from support in Enum.GetValues<DatabaseDeploymentSupport>()
        from mode in new[] { DatabaseDeploymentMode.Unspecified, DatabaseDeploymentMode.SingleInstance,
            DatabaseDeploymentMode.MultiInstance, (DatabaseDeploymentMode)99 }
        from registered in new[] { true, false }
        select new object[] { support, mode, registered };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Pure_validation_and_orchestration_share_the_permission_matrix_before_IO(
        DatabaseDeploymentSupport support, DatabaseDeploymentMode mode, bool registered)
    {
        var capability = new DeploymentProvider(support: support);
        var registry = Registry(registered ? [capability] : []);
        var validator = new DatabaseDeploymentValidator(registry);
        var allowed = registered && (mode == DatabaseDeploymentMode.SingleInstance ||
            mode == DatabaseDeploymentMode.MultiInstance && support == DatabaseDeploymentSupport.SingleAndMultiInstance);
        var validation = validator.Validate("customdb", mode);
        Assert.Equal(allowed, validation.IsSupported);
        Assert.Equal(allowed ? null : WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported, validation.PreparationErrorCode);
        Assert.Equal(allowed ? null : WellKnownMigrationErrorCodes.LockNotSupported, validation.MigrationErrorCode);
        Assert.Equal(0, capability.IdentityCalls);
        AssertSafe(validation);

        if (!allowed)
        {
            var executor = new FakeMigrationExecutor();
            var realLock = new FakeMigrationLockProvider("CustomDb");
            var orchestrator = new DatabaseMigrationOrchestrator(executor, Locks(realLock), registry);
            var result = await orchestrator.OrchestrateMigrationAsync(Service, Target(), mode, Timeout, Token);
            Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
            Assert.Equal(0, capability.IdentityCalls);
            Assert.Equal(0, realLock.AcquireAttempts);
            Assert.Equal(0, executor.InspectCallCount);
            Assert.Equal(0, executor.ExecuteCallCount);
            AssertSafe(result);
        }
    }

    [Fact]
    public void Registry_captures_declarations_and_aliases_without_implying_other_capabilities()
    {
        var resolver = new BootstrapDatabaseProviderRegistry([new AliasBootstrapProvider()]).ProviderIdResolver;
        var provider = new DeploymentProvider("alias");
        var registry = new DatabaseDeploymentCapabilityRegistry([provider], resolver);
        provider.Capability = new("Changed", DatabaseDeploymentSupport.SingleAndMultiInstance);
        Assert.True(registry.TryGetCapability("ALIAS", out var captured));
        Assert.Equal("CustomDb", captured!.ProviderId);
        Assert.Equal(DatabaseDeploymentSupport.SingleInstanceOnly, captured.Support);
        Assert.False(new DatabaseDeploymentValidator(registry).Validate("customdb", DatabaseDeploymentMode.MultiInstance).IsSupported);
        Assert.False(new DatabaseDeploymentValidator(Registry()).Validate("alias", DatabaseDeploymentMode.SingleInstance).IsSupported);
        Assert.Throws<ArgumentException>(() => new DatabaseDeploymentCapabilityRegistry(
            [new DeploymentProvider("alias"), new DeploymentProvider("CustomDb")], resolver));
    }

    [Fact]
    public void Invalid_declarations_and_ids_fail_without_echoing_target_data()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseDeploymentCapability("CustomDb", (DatabaseDeploymentSupport)42));
        Assert.Throws<ArgumentException>(() => Registry(new DeploymentProvider(), new DeploymentProvider("customdb")));
        Assert.Throws<ArgumentException>(() => Registry((IDatabaseDeploymentCapabilityProvider)null!));
        var validator = new DatabaseDeploymentValidator(Registry(new DeploymentProvider()));
        foreach (var id in new[] { null, "", "missing", Secret })
        {
            var result = validator.Validate(id, DatabaseDeploymentMode.SingleInstance);
            Assert.False(result.IsSupported);
            AssertSafe(result);
        }
    }

    [Fact]
    public async Task Single_instance_does_not_acquire_or_construct_a_distributed_lease()
    {
        var executor = new FakeMigrationExecutor([MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible]);
        var provider = new DeploymentProvider();
        var realLock = new FakeMigrationLockProvider("CustomDb", acquireException: new InvalidOperationException(Secret));
        var result = await new DatabaseMigrationOrchestrator(executor, Locks(realLock), Registry(provider))
            .OrchestrateMigrationAsync(Service, Target(), DatabaseDeploymentMode.SingleInstance, Timeout, Token);
        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(2, executor.InspectCallCount);
        Assert.Equal(1, executor.ExecuteCallCount);
        Assert.Equal(0, realLock.AcquireAttempts);
        Assert.Equal(0, realLock.LeaseDisposeCount);
    }

    [Fact]
    public async Task Old_API_and_multi_instance_require_real_locks_even_with_deployment_capability()
    {
        var executor = new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible);
        var provider = new DeploymentProvider(support: DatabaseDeploymentSupport.SingleAndMultiInstance);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, Locks(), Registry(provider));
        var old = await orchestrator.OrchestrateMigrationAsync(Service, Target(), Timeout, Token);
        var multi = await orchestrator.OrchestrateMigrationAsync(Service, Target(), DatabaseDeploymentMode.MultiInstance, Timeout, Token);
        var undeclared = await new DatabaseMigrationOrchestrator(executor, Locks())
            .OrchestrateMigrationAsync(Service, Target(), DatabaseDeploymentMode.SingleInstance, Timeout, Token);
        foreach (var result in new[] { old, multi, undeclared }) Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(0, executor.InspectCallCount);
        Assert.Equal(0, provider.IdentityCalls);

        var realLock = new FakeMigrationLockProvider("CustomDb");
        var supported = await new DatabaseMigrationOrchestrator(executor, Locks(realLock), Registry(provider))
            .OrchestrateMigrationAsync(Service, Target(), DatabaseDeploymentMode.MultiInstance, Timeout, Token);
        Assert.True(supported.Succeeded);
        Assert.Equal(1, realLock.AcquireAttempts);
        Assert.Equal(1, realLock.LeaseDisposeCount);
        Assert.Equal(0, provider.IdentityCalls);
    }

    [Theory]
    [InlineData(MigrationObservationState.Empty, null, true)]
    [InlineData(MigrationObservationState.PendingMigration, null, true)]
    [InlineData(MigrationObservationState.CurrentVersionCompatible, null, false)]
    [InlineData(MigrationObservationState.VersionTooNew, WellKnownMigrationErrorCodes.VersionTooNew, false)]
    [InlineData(MigrationObservationState.InspectionFailed, WellKnownMigrationErrorCodes.InspectionFailed, false)]
    [InlineData((MigrationObservationState)999, WellKnownMigrationErrorCodes.InspectionFailed, false)]
    public async Task Single_instance_initial_state_permits_only_explicit_migration_states(MigrationObservationState initial, string? error, bool execute)
    {
        var executor = new FakeMigrationExecutor([initial, MigrationObservationState.CurrentVersionCompatible]);
        var result = await Run(executor, new DeploymentProvider());
        Assert.Equal(error, result.ErrorCode);
        Assert.Equal(execute, result.ExecutorWasCalled);
        Assert.Equal(execute ? 2 : 1, executor.InspectCallCount);
        Assert.Equal(execute ? 1 : 0, executor.ExecuteCallCount);
    }

    [Theory]
    [InlineData(MigrationObservationState.Empty)]
    [InlineData(MigrationObservationState.PendingMigration)]
    [InlineData(MigrationObservationState.VersionTooNew)]
    [InlineData(MigrationObservationState.InspectionFailed)]
    [InlineData((MigrationObservationState)999)]
    public async Task Final_inspection_is_required_for_success(MigrationObservationState final)
    {
        var executor = new FakeMigrationExecutor([MigrationObservationState.Empty, final]);
        var result = await Run(executor, new DeploymentProvider());
        Assert.Equal(WellKnownMigrationErrorCodes.FinalStateInvalid, result.ErrorCode);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal(2, executor.InspectCallCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Stage_failures_and_internal_cancellation_are_safe_and_release_the_turn(int stage, bool internalCancel)
    {
        Exception failure = internalCancel ? new OperationCanceledException(Secret) : new InvalidOperationException(Secret);
        var executor = StageExecutor(stage, () => throw failure);
        var provider = new DeploymentProvider();
        var result = await Run(executor, provider);
        Assert.Equal(stage == 1 ? WellKnownMigrationErrorCodes.ExecutionFailed : WellKnownMigrationErrorCodes.InspectionFailed, result.ErrorCode);
        Assert.Equal(stage != 0, result.ExecutorWasCalled);
        AssertSafe(result);
        Assert.True((await Run(new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible), provider)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Caller_cancellation_in_each_stage_preserves_token_without_secret_inner_exceptions(int stage)
    {
        using var source = new CancellationTokenSource();
        var provider = new DeploymentProvider();
        var executor = StageExecutor(stage, () => { source.Cancel(); throw new InvalidOperationException(Secret); });
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => Run(executor, provider, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertSafe(exception);
        Assert.True((await Run(new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible), provider)).Succeeded);
    }

    [Theory]
    [InlineData("empty", WellKnownMigrationErrorCodes.LockNotSupported)]
    [InlineData("failure", WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData("internal-cancel", WellKnownMigrationErrorCodes.LockFailed)]
    [InlineData("timeout", WellKnownMigrationErrorCodes.LockTimeout)]
    public async Task Identity_failures_do_not_invoke_the_executor(string failure, string expected)
    {
        var provider = new DeploymentProvider
        {
            Resolve = async (_, token) =>
            {
                if (failure == "timeout") await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                return failure switch
                {
                    "empty" => "",
                    "internal-cancel" => throw new OperationCanceledException(Secret),
                    _ => throw new InvalidOperationException(Secret)
                };
            }
        };
        var executor = new FakeMigrationExecutor();
        var result = await Run(executor, provider, timeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal(expected, result.ErrorCode);
        Assert.Equal(0, executor.InspectCallCount);
        AssertSafe(result);
    }

    [Fact]
    public async Task Same_provider_and_target_serialize_across_registries_service_ids_aliases_and_password_changes()
    {
        var targetIdentity = Guid.NewGuid().ToString();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = MigrationObservationState.PendingMigration;
        var executions = 0;
        var firstExecutor = new CallbackExecutor(() => state, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Increment(ref executions);
            state = MigrationObservationState.CurrentVersionCompatible;
        });
        var secondExecutor = new CallbackExecutor(() => state, _ => { Interlocked.Increment(ref executions); return Task.CompletedTask; });
        var first = Run(firstExecutor, new DeploymentProvider(identity: targetIdentity));
        await entered.Task.WaitAsync(Token);
        var resolver = new BootstrapDatabaseProviderRegistry([new AliasBootstrapProvider()]).ProviderIdResolver;
        var second = new DatabaseMigrationOrchestrator(secondExecutor, Locks(),
            new DatabaseDeploymentCapabilityRegistry([new DeploymentProvider(identity: targetIdentity)], resolver))
            .OrchestrateMigrationAsync(ServiceId.Parse("other-service"), Target("ALIAS", "changed"),
                DatabaseDeploymentMode.SingleInstance, Timeout, Token).AsTask();
        Assert.Equal(0, secondExecutor.Inspections);
        Assert.False(second.IsCompleted);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Timeout, Token);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.True(results[0].ExecutorWasCalled);
        Assert.False(results[1].ExecutorWasCalled);
        Assert.Equal(1, executions);
        Assert.Equal(1, secondExecutor.Inspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_targets_or_providers_do_not_block_each_other(bool changeProvider)
    {
        var identity = Guid.NewGuid().ToString();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Run(new CallbackExecutor(() => MigrationObservationState.PendingMigration, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }), new DeploymentProvider(identity: identity));
        await entered.Task.WaitAsync(Token);
        try
        {
            var capability = new DeploymentProvider(changeProvider ? "OtherDb" : "CustomDb", identity: changeProvider ? identity : identity + "-other");
            var result = await Run(new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible), capability).WaitAsync(Timeout, Token);
            Assert.True(result.Succeeded);
            Assert.False(holding.IsCompleted);
        }
        finally { release.SetResult(); await holding; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_timeout_or_cancellation_does_not_enter_stages_or_poison_later_calls(bool cancel)
    {
        var capability = new DeploymentProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Run(new CallbackExecutor(() => MigrationObservationState.PendingMigration, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }), capability);
        await entered.Task.WaitAsync(Token);
        try
        {
            using var source = new CancellationTokenSource();
            var waiterExecutor = new FakeMigrationExecutor();
            var waiting = Run(waiterExecutor, capability, source.Token, cancel ? Timeout : TimeSpan.FromMilliseconds(30));
            if (cancel)
            {
                source.Cancel();
                var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
                Assert.Equal(source.Token, exception.CancellationToken);
            }
            else Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, (await waiting).ErrorCode);
            Assert.Equal(0, waiterExecutor.InspectCallCount);
        }
        finally { release.SetResult(); await holding; }
        Assert.True((await Run(new FakeMigrationExecutor(MigrationObservationState.CurrentVersionCompatible), capability)).Succeeded);
    }

    [Fact]
    public async Task Precancelled_request_and_identity_cancellation_preserve_the_original_token()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var capability = new DeploymentProvider();
        var executor = new FakeMigrationExecutor();
        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => Run(executor, capability, source.Token));
        Assert.Equal(source.Token, cancelled.CancellationToken);
        Assert.Equal(0, capability.IdentityCalls);
        Assert.Equal(0, executor.InspectCallCount);

        using var duringIdentity = new CancellationTokenSource();
        var failing = new DeploymentProvider
        {
            Resolve = (_, _) =>
            {
                duringIdentity.Cancel();
                throw new OperationCanceledException(Secret);
            }
        };
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => Run(executor, failing, duringIdentity.Token));
        Assert.Equal(duringIdentity.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertSafe(exception);
        Assert.Equal(0, executor.InspectCallCount);
    }

    [Fact]
    public async Task Acquired_turn_does_not_apply_the_queue_deadline_to_execution()
    {
        var executor = new FakeMigrationExecutor(
            [MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible],
            executeDelay: token => Task.Delay(TimeSpan.FromMilliseconds(150), token));
        var result = await Run(executor, new DeploymentProvider(), timeout: TimeSpan.FromMilliseconds(100));
        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
    }

    private static FakeMigrationExecutor StageExecutor(int stage, Action action) => new(
        [MigrationObservationState.PendingMigration, MigrationObservationState.CurrentVersionCompatible],
        executeDelay: _ => { if (stage == 1) action(); return Task.CompletedTask; },
        inspectDelay: (call, _) => { if (stage == 0 && call == 1 || stage == 2 && call == 2) action(); return Task.CompletedTask; });

    private static Task<MigrationExecutionResult> Run(IDatabaseMigrationExecutor executor, DeploymentProvider provider,
        CancellationToken? token = null, TimeSpan? timeout = null) =>
        new DatabaseMigrationOrchestrator(executor, Locks(), Registry(provider)).OrchestrateMigrationAsync(
            Service, Target(provider.Capability.ProviderId), DatabaseDeploymentMode.SingleInstance, timeout ?? Timeout, token ?? Token).AsTask();

    private static DatabaseDeploymentCapabilityRegistry Registry(params IDatabaseDeploymentCapabilityProvider[] providers) =>
        new(providers, DatabaseProviderIdResolver.Empty);
    private static DatabaseMigrationLockProviderRegistry Locks(params IDatabaseMigrationLockProvider[] providers) =>
        new(providers, DatabaseProviderIdResolver.Empty);
    private static void AssertSafe(object value)
    {
        var text = value + (value is Exception ? "" : JsonSerializer.Serialize(value));
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("deployment-secret", text, StringComparison.Ordinal);
    }

    private sealed class AliasBootstrapProvider : IBootstrapDatabaseProvider
    {
        public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new("CustomDb", "Custom",
            BootstrapDatabaseTargetKind.File, BootstrapServerVersionRequirement.Optional, ["alias"]);
        public ValueTask<BootstrapValidationResult> ValidateAsync(BootstrapDatabaseConfiguration database,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Validation must not be called.");
    }

    private sealed class DeploymentProvider(string provider = "CustomDb", DatabaseDeploymentSupport support = DatabaseDeploymentSupport.SingleInstanceOnly,
        string? identity = null) : IDatabaseDeploymentCapabilityProvider
    {
        private readonly string identity = identity ?? Guid.NewGuid().ToString();
        public DatabaseDeploymentCapability Capability { get; set; } = new(provider, support);
        internal int IdentityCalls;
        internal Func<BootstrapDatabaseConfiguration, CancellationToken, ValueTask<string>>? Resolve { get; init; }
        public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref IdentityCalls);
            return Resolve?.Invoke(target, cancellationToken) ?? ValueTask.FromResult(identity);
        }
    }

    private sealed class CallbackExecutor(Func<MigrationObservationState> inspect, Func<CancellationToken, Task> execute) : IDatabaseMigrationExecutor
    {
        internal int Inspections;
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Inspections);
            return ValueTask.FromResult(inspect());
        }
        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => await execute(cancellationToken);
    }
}

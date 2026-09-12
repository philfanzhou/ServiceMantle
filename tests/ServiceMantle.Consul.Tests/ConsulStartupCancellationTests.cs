using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using ServiceMantle.Health;
using Xunit;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// Drives the completion checkpoints of <see cref="ConsulRegistrationLifecycle.StartAsync"/>:
/// a caller cancellation observed after client creation settles must outrank the disabled result,
/// every configuration failure category, and a produced session. Uses this file's own accessor
/// and factory doubles; the shared fixture only materializes real snapshots.
/// </summary>
public sealed class ConsulStartupCancellationTests
{
    private const string Canary = "startup-failure-canary";

    [Fact]
    public async Task Pre_cancelled_start_reads_no_snapshot_and_resolves_no_factory()
    {
        await using var rig = Rig.Create(await SnapshotForAsync(enabled: true));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Lifecycle.StartAsync(cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, rig.Accessor.Reads);
        Assert.Equal(0, rig.Factory.Calls);
        Assert.Equal(0, rig.Source.Calls);
    }

    public static TheoryData<string> CancelledCompletions => new()
    {
        // The accessor cancels the caller, then returns a disabled snapshot.
        "disabled-snapshot",
        // The accessor cancels the caller, then reports no active snapshot.
        "no-snapshot",
        // The accessor cancels the caller, then fails ordinarily.
        "accessor-ordinary-failure",
        // The accessor cancels the caller, then throws its own cancellation with a foreign token.
        "accessor-internal-cancellation",
        // The factory cancels the caller, then returns no client.
        "factory-null",
        // The factory cancels the caller, then fails ordinarily.
        "factory-ordinary-failure",
        // The factory cancels the caller, then delivers a working client.
        "factory-success"
    };

    [Theory]
    [MemberData(nameof(CancelledCompletions))]
    public async Task Every_client_creation_completion_after_cancellation_ends_in_a_safe_OCE(
        string mode)
    {
        using var cts = new CancellationTokenSource();
        var disabled = mode == "disabled-snapshot";
        await using var rig = Rig.Create(
            await SnapshotForAsync(enabled: !disabled),
            accessor =>
            {
                accessor.OnRead = cts.Cancel;
                switch (mode)
                {
                    case "no-snapshot":
                        accessor.NoSnapshot = true;
                        break;
                    case "accessor-ordinary-failure":
                        accessor.Failure = new InvalidOperationException(Canary);
                        break;
                    case "accessor-internal-cancellation":
                        accessor.Failure = new OperationCanceledException(
                            Canary,
                            new InvalidOperationException(Canary),
                            new CancellationTokenSource().Token);
                        break;
                }
            },
            factory =>
            {
                if (mode is not ("factory-null" or "factory-ordinary-failure" or "factory-success"))
                {
                    return;
                }

                factory.OnCreate = cts.Cancel;
                if (mode == "factory-null")
                {
                    factory.Client = null;
                }
                else if (mode == "factory-ordinary-failure")
                {
                    factory.Failure = new InvalidOperationException(Canary);
                }
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Lifecycle.StartAsync(cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rig.Source.Calls);
        Assert.Equal(0, rig.Client.Registers);
        Assert.Equal(0, rig.Client.Deregisters);
        Assert.Equal(0, rig.Lifecycle.SnapshotVersion);
        if (mode == "factory-success")
        {
            // The produced session was handed to the lifecycle owner's disposal rule exactly once.
            Assert.Equal(1, rig.Client.Disposals);
        }
        else
        {
            Assert.Equal(0, rig.Client.Disposals);
        }
    }

    [Fact]
    public async Task A_failed_disposal_of_a_cancelled_session_does_not_mask_the_caller_OCE()
    {
        using var cts = new CancellationTokenSource();
        await using var rig = Rig.Create(
            await SnapshotForAsync(enabled: true),
            configureFactory: configure => configure.OnCreate = cts.Cancel,
            configureClient: client => client.FailDisposal = true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Lifecycle.StartAsync(cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, rig.Client.Disposals);
        var diagnostic = Assert.Single(rig.Observer.Diagnostics);
        Assert.Equal(ConsulLifecycleDiagnostics.SessionDisposalFailed, diagnostic.Classification);
    }

    [Fact]
    public async Task Uncancelled_starts_keep_the_existing_outcomes()
    {
        await using var enabled = Rig.Create(await SnapshotForAsync(enabled: true));
        await enabled.Lifecycle.StartAsync(CancellationToken.None);
        await ConsulLifecycleHarness.WaitAsync(
            () => enabled.Client.Registers == 1,
            "an uncancelled enabled start never registered");

        await using var disabled = Rig.Create(await SnapshotForAsync(enabled: false));
        await disabled.Lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ConsulLifecycleState.Disabled, disabled.Lifecycle.State);
        Assert.Equal(0, disabled.Client.Registers);
        Assert.Equal(0, disabled.Client.Disposals);

        await using var noSnapshot = Rig.Create(null, accessor => accessor.NoSnapshot = true);
        await AssertConsulFailureAsync(
            noSnapshot, ConsulConfigurationError.SnapshotUnavailable);

        await using var accessorFailure = Rig.Create(
            await SnapshotForAsync(enabled: true),
            accessor => accessor.Failure = new InvalidOperationException(Canary));
        await AssertConsulFailureAsync(
            accessorFailure, ConsulConfigurationError.SnapshotUnavailable);

        await using var factoryNull = Rig.Create(
            await SnapshotForAsync(enabled: true), configureFactory: factory => factory.Client = null);
        await AssertConsulFailureAsync(
            factoryNull, ConsulConfigurationError.ClientCreationFailed);

        await using var factoryFailure = Rig.Create(
            await SnapshotForAsync(enabled: true),
            configureFactory: factory => factory.Failure = new InvalidOperationException(Canary));
        await AssertConsulFailureAsync(
            factoryFailure, ConsulConfigurationError.ClientCreationFailed);

        await using var foreign = Rig.Create(
            await SnapshotForAsync(enabled: true, ServiceId.Parse("foreign-service")));
        await AssertConsulFailureAsync(
            foreign, ConsulConfigurationError.InvalidConfiguration);
    }

    [Fact]
    public async Task Two_independent_lifecycles_only_cancel_the_affected_one()
    {
        using var cts = new CancellationTokenSource();
        await using var cancelled = Rig.Create(
            await SnapshotForAsync(enabled: true),
            accessor => accessor.OnRead = cts.Cancel);
        await using var healthy = Rig.Create(await SnapshotForAsync(enabled: true));

        var cancelledStart = Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelled.Lifecycle.StartAsync(cts.Token));
        await healthy.Lifecycle.StartAsync(CancellationToken.None);
        await ConsulLifecycleHarness.WaitAsync(
            () => healthy.Client.Registers == 1,
            "the uncancelled lifecycle never registered");

        Assert.Equal(cts.Token, (await cancelledStart).CancellationToken);
        Assert.Equal(0, cancelled.Client.Registers);
    }

    private static async Task AssertConsulFailureAsync(Rig rig, ConsulConfigurationError error)
    {
        var exception = await Assert.ThrowsAsync<ConsulConfigurationException>(() =>
            rig.Lifecycle.StartAsync(CancellationToken.None));
        Assert.Equal(error, exception.Error);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Source.Calls);
    }

    /// <summary>Materializes one genuine snapshot through the shared fixture.</summary>
    private static async Task<ServiceSettingSnapshot> SnapshotForAsync(
        bool enabled,
        ServiceId? service = null)
    {
        using var fixture = service is null
            ? new ConsulFixture()
            : new ConsulFixture(snapshotService: service) { SnapshotService = service };
        var raw = ConsulFixture.Enabled();
        if (!enabled)
        {
            raw[ConsulSettingDefinitions.Enabled] = "false";
        }

        await fixture.ActivateAsync(raw);
        Assert.True(fixture.Accessor.TryGetCurrent(out var snapshot));
        return snapshot!;
    }

    /// <summary>
    /// One lifecycle wired to this file's own accessor and factory doubles, a counting readiness
    /// source, and the shared scripted client and fake clock seams.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private Rig(
            ConsulRegistrationLifecycle lifecycle,
            StartupAccessor accessor,
            StartupFactory factory,
            ConsulLifecycleHarness.ScriptedClient client,
            CountingDecisionSource source,
            ConsulLifecycleObserver observer,
            ServiceProvider services)
        {
            Lifecycle = lifecycle;
            Accessor = accessor;
            Factory = factory;
            Client = client;
            Source = source;
            Observer = observer;
            this.services = services;
        }

        internal ConsulRegistrationLifecycle Lifecycle { get; }

        internal StartupAccessor Accessor { get; }

        internal StartupFactory Factory { get; }

        internal ConsulLifecycleHarness.ScriptedClient Client { get; }

        internal CountingDecisionSource Source { get; }

        internal ConsulLifecycleObserver Observer { get; }

        internal static Rig Create(
            ServiceSettingSnapshot? snapshot,
            Action<StartupAccessor>? configureAccessor = null,
            Action<StartupFactory>? configureFactory = null,
            Action<ConsulLifecycleHarness.ScriptedClient>? configureClient = null)
        {
            var accessor = new StartupAccessor { Snapshot = snapshot };
            configureAccessor?.Invoke(accessor);
            var client = new ConsulLifecycleHarness.ScriptedClient();
            configureClient?.Invoke(client);
            var factory = new StartupFactory { Client = client };
            configureFactory?.Invoke(factory);
            var source = new CountingDecisionSource();
            var services = new ServiceCollection()
                .AddScoped<IServiceReadinessDecisionSource>(_ => source)
                .BuildServiceProvider();
            var provider = new ConsulClientProvider(
                accessor,
                ConsulFixture.Service,
                ConsulFixture.Instance,
                () => factory);
            var observer = new ConsulLifecycleObserver();
            var lifecycle = new ConsulRegistrationLifecycle(
                provider,
                services.GetRequiredService<IServiceScopeFactory>(),
                new ConsulLifecycleOptions().Validate(),
                new ManualTimeProvider(),
                observer);
            return new Rig(lifecycle, accessor, factory, client, source, observer, services);
        }

        public async ValueTask DisposeAsync()
        {
            await Lifecycle.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    /// <summary>Serves a snapshot, a failure, or nothing, and can cancel the start caller on read.</summary>
    private sealed class StartupAccessor : IServiceSettingCurrentSnapshotAccessor
    {
        internal ServiceSettingSnapshot? Snapshot { get; set; }

        internal Exception? Failure { get; set; }

        internal bool NoSnapshot { get; set; }

        internal Action? OnRead { get; set; }

        internal int Reads { get; private set; }

        public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot)
        {
            Reads++;
            OnRead?.Invoke();
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (NoSnapshot)
            {
                snapshot = null;
                return false;
            }

            snapshot = Snapshot;
            return snapshot is not null;
        }
    }

    /// <summary>Delivers a client, none, or a failure, and can cancel the start caller on create.</summary>
    private sealed class StartupFactory : IConsulClientFactory
    {
        internal IConsulClient? Client { get; set; }

        internal Exception? Failure { get; set; }

        internal Action? OnCreate { get; set; }

        internal int Calls { get; private set; }

        public IConsulClient Create(ConsulClientConfiguration configuration)
        {
            Calls++;
            OnCreate?.Invoke();
            if (Failure is { } failure)
            {
                throw failure;
            }

            return Client!;
        }
    }

    /// <summary>Counts sampler-driven decision calls; the loops never run when start is cancelled.</summary>
    private sealed class CountingDecisionSource : IServiceReadinessDecisionSource
    {
        internal int Calls { get; private set; }

        public ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ConsulLifecycleHarness.Ready());
        }
    }
}

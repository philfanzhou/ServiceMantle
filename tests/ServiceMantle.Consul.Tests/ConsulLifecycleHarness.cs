using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// Builds one Consul registration lifecycle on a fake clock, a scripted readiness decision source,
/// and a scripted client, and owns everything it creates.
/// </summary>
internal sealed class ConsulLifecycleHarness : IAsyncDisposable
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private readonly ConsulFixture fixture;

    private ConsulLifecycleHarness(
        ConsulFixture fixture,
        ConsulRegistrationLifecycle lifecycle,
        ScriptedDecisionSource decisions,
        ScriptedClient client,
        ManualTimeProvider time,
        ConsulLifecycleObserver observer)
    {
        this.fixture = fixture;
        Lifecycle = lifecycle;
        Decisions = decisions;
        Client = client;
        Time = time;
        Observer = observer;
    }

    internal ConsulRegistrationLifecycle Lifecycle { get; }

    internal ScriptedDecisionSource Decisions { get; }

    internal ScriptedClient Client { get; }

    internal ManualTimeProvider Time { get; }

    internal ConsulLifecycleObserver Observer { get; }

    internal ConsulFixture Fixture => fixture;

    internal static ServiceReadinessDecision Ready() => ServiceReadinessDecision.Ready(ReadySnapshot);

    internal static ServiceReadinessDecision NotReady(
        ServiceStartupPhase phase = ServiceStartupPhase.PendingSetup,
        ServiceMigrationReadinessState migration = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState database = ServiceDatabaseReadinessState.Reachable,
        string? errorCode = null) =>
        ServiceReadinessDecision.NotReady(
            new ServiceHealthSnapshot(phase, migration, database),
            errorCode);

    internal static async Task<ConsulLifecycleHarness> CreateAsync(
        bool enabled = true,
        bool activate = true,
        Action<ConsulLifecycleOptions>? configure = null,
        ScriptedDecisionSource? decisions = null,
        ScriptedClient? client = null)
    {
        var resolvedDecisions = decisions ?? new ScriptedDecisionSource();
        var resolvedClient = client ?? new ScriptedClient();
        var fixture = new ConsulFixture(configureServices: services =>
            services.AddScoped<IServiceReadinessDecisionSource>(_ =>
            {
                resolvedDecisions.Resolutions++;
                return resolvedDecisions;
            }));
        fixture.ClientFactory.CreateClient = _ => resolvedClient;
        var raw = ConsulFixture.Enabled();
        if (!enabled)
        {
            raw[ConsulSettingDefinitions.Enabled] = "false";
        }

        if (activate)
        {
            await fixture.ActivateAsync(raw);
        }

        var options = new ConsulLifecycleOptions();
        configure?.Invoke(options);
        var time = new ManualTimeProvider();
        var observer = new ConsulLifecycleObserver();
        var lifecycle = new ConsulRegistrationLifecycle(
            fixture.Provider,
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            options.Validate(),
            time,
            observer);
        return new ConsulLifecycleHarness(fixture, lifecycle, resolvedDecisions, resolvedClient, time, observer);
    }

    internal Task StartAsync() => Lifecycle.StartAsync(CancellationToken.None);

    internal Task StartWithTokenAsync(CancellationToken cancellationToken) =>
        Lifecycle.StartAsync(cancellationToken);

    internal Task StopAsync() => Lifecycle.StopAsync(CancellationToken.None);

    internal Task StopWithTokenAsync(CancellationToken cancellationToken) =>
        Lifecycle.StopAsync(cancellationToken);

    /// <summary>
    /// Waits until a condition holds. Virtual time drives the outcome; the real-time bound only
    /// turns a regression into a failure instead of a hang.
    /// </summary>
    internal static async Task WaitAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Repeatedly advances the clock until a condition holds. Used where the test only needs the
    /// poll loop to run again, never where an exact delay is the assertion.
    /// </summary>
    internal async Task AdvanceUntilAsync(TimeSpan step, Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            Time.Advance(step);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the loops are idle, so an advance cannot race an in-flight step.</summary>
    internal Task QuiesceAsync() => WaitAsync(
        () => Lifecycle.State is ConsulLifecycleState.Registered or ConsulLifecycleState.NotReady
            or ConsulLifecycleState.Backoff or ConsulLifecycleState.Disabled,
        "the lifecycle never reached a settled state");

    public async ValueTask DisposeAsync()
    {
        await Lifecycle.DisposeAsync();
        fixture.Dispose();
    }

    /// <summary>Answers scripted readiness decisions and counts scope resolutions.</summary>
    internal sealed class ScriptedDecisionSource : IServiceReadinessDecisionSource
    {
        private int calls;
        private ServiceReadinessDecision? current = Ready();

        internal int Resolutions;

        internal int Calls => Volatile.Read(ref calls);

        /// <summary>The decision every call answers with; null exercises an invalid source.</summary>
        internal ServiceReadinessDecision? Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        /// <summary>Set to throw instead of answering.</summary>
        internal Exception? Failure { get; set; }

        /// <summary>Set to wait on the sampler token instead of answering.</summary>
        internal bool Hang { get; set; }

        public async ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return Current!;
        }
    }

    /// <summary>Records every remote operation, its overlap, and answers a scripted outcome.</summary>
    internal sealed class ScriptedClient : IConsulClient
    {
        private readonly List<string> operations = [];
        private int active;
        private int registers;
        private int deregisters;

        internal int MaximumConcurrent { get; private set; }

        internal int Registers => Volatile.Read(ref registers);

        internal int Deregisters => Volatile.Read(ref deregisters);

        internal bool Disposed { get; private set; }

        internal int Disposals { get; private set; }

        /// <summary>Set to make Dispose throw.</summary>
        internal bool FailDisposal { get; set; }

        /// <summary>Answers the outcome of the call with the given 1-based number.</summary>
        internal Func<string, int, ConsulClientResult> Outcome { get; set; } =
            (_, _) => ConsulClientResult.Success;

        /// <summary>Set to throw from the operation instead of answering.</summary>
        internal Func<string, int, Exception?> Failure { get; set; } = (_, _) => null;

        /// <summary>Set to hold every operation until the gate is released.</summary>
        internal TaskCompletionSource? Gate { get; set; }

        /// <summary>
        /// Set to block the owner loop synchronously on the way into an operation, before it can
        /// reach its wait loop. It lets a test drive the sampler and the stop while the owner is
        /// provably parked outside the desire semaphore.
        /// </summary>
        internal ManualResetEventSlim? Hold { get; set; }

        internal TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyList<string> Operations
        {
            get
            {
                lock (operations)
                {
                    return operations.ToArray();
                }
            }
        }

        internal void Arm() => Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string? LastRegistrationId { get; private set; }

        public ValueTask<ConsulClientResult> RegisterAsync(
            ConsulServiceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            LastRegistrationId = registration.Id;
            return InvokeAsync("register", Interlocked.Increment(ref registers), cancellationToken);
        }

        public ValueTask<ConsulClientResult> DeregisterAsync(
            string registrationId,
            CancellationToken cancellationToken = default)
        {
            LastRegistrationId = registrationId;
            return InvokeAsync("deregister", Interlocked.Increment(ref deregisters), cancellationToken);
        }

        public void Dispose()
        {
            Disposals++;
            Disposed = true;
            if (FailDisposal)
            {
                throw new InvalidOperationException("consul-secret-do-not-project");
            }
        }

        private async ValueTask<ConsulClientResult> InvokeAsync(
            string kind,
            int call,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref active);
            lock (operations)
            {
                operations.Add(kind);
                MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent);
            }

            try
            {
                // Captured on entry so a test that parks this call on Hold can still retarget Gate
                // for the operations that follow it.
                var pending = Gate;
                Entered.TrySetResult();
                Hold?.Wait(TimeSpan.FromSeconds(20));
                if (pending is not null)
                {
                    await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Failure(kind, call) is { } failure)
                {
                    throw failure;
                }

                return Outcome(kind, call);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Consul;
using ServiceMantle.Health;
using Xunit;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// Drives the completion checkpoints of one readiness sample: a budget exhausted by the decision
/// call, by the scope disposal, or together with any decision or failure must never publish Ready.
/// Uses the shared fake clock and fixture seams with this file's own decision source.
/// </summary>
public sealed class ConsulReadinessCompletionTests
{
    [Fact]
    public async Task Ready_returned_after_the_call_exhausted_the_budget_is_rejected()
    {
        await using var rig = await Rig.CreateAsync(source => source.AdvanceOnCall = Rig.Budget);
        await rig.StartAsync();
        await rig.WaitSampleSettledAsync();
        await rig.WaitTerminalDiagnosticRecordedAsync();

        Assert.Equal(0, rig.Client.Registers);
        Assert.Empty(rig.Client.Operations);
        Assert.Equal(ConsulLifecycleState.NotReady, rig.Lifecycle.State);
        Assert.Equal(ConsulRemotePresence.Absent, rig.Lifecycle.Presence);
        var diagnostic = Assert.Single(rig.Observer.Diagnostics);
        Assert.Equal(ConsulLifecycleDiagnostics.ReadinessTimeout, diagnostic.Classification);
    }

    [Fact]
    public async Task Ready_returned_before_the_scope_disposal_exhausted_the_budget_is_rejected()
    {
        await using var rig = await Rig.CreateAsync(source => source.AdvanceOnDispose = Rig.Budget);
        await rig.StartAsync();
        await rig.WaitSampleSettledAsync();
        await rig.WaitTerminalDiagnosticRecordedAsync();

        Assert.Equal(0, rig.Client.Registers);
        Assert.Equal(ConsulLifecycleState.NotReady, rig.Lifecycle.State);
        var diagnostic = Assert.Single(rig.Observer.Diagnostics);
        Assert.Equal(ConsulLifecycleDiagnostics.ReadinessTimeout, diagnostic.Classification);
    }

    public static TheoryData<string> ExpiredOutcomes => new()
    {
        // The decision call itself runs past the budget and then returns each outcome.
        "ready-after-call",
        "not-ready-after-call",
        // The decision returns normally and the scope disposal exhausts the budget.
        "ready-on-dispose",
        "null-on-dispose",
        // Failures that arrive together with an exhausted budget.
        "ordinary-exception-after-call",
        "internal-cancellation-after-call"
    };

    [Theory]
    [MemberData(nameof(ExpiredOutcomes))]
    public async Task Every_expired_completion_fails_closed_with_one_readiness_timeout(string mode)
    {
        await using var rig = await Rig.CreateAsync(source =>
        {
            switch (mode)
            {
                case "ready-after-call":
                    source.AdvanceOnCall = Rig.Budget;
                    break;
                case "not-ready-after-call":
                    source.Decision = ConsulLifecycleHarness.NotReady();
                    source.AdvanceOnCall = Rig.Budget;
                    break;
                case "ready-on-dispose":
                    source.AdvanceOnDispose = Rig.Budget;
                    break;
                case "null-on-dispose":
                    source.Decision = null;
                    source.AdvanceOnDispose = Rig.Budget;
                    break;
                case "ordinary-exception-after-call":
                    source.Failure = new InvalidOperationException(ConsulFixture.Secret);
                    source.AdvanceOnCall = Rig.Budget;
                    break;
                default:
                    source.Failure = new OperationCanceledException(new CancellationTokenSource().Token);
                    source.AdvanceOnCall = Rig.Budget;
                    break;
            }
        });
        await rig.StartAsync();
        await rig.WaitSampleSettledAsync();
        await rig.WaitTerminalDiagnosticRecordedAsync();

        Assert.Equal(0, rig.Client.Registers);
        Assert.Equal(ConsulLifecycleState.NotReady, rig.Lifecycle.State);
        var diagnostic = Assert.Single(rig.Observer.Diagnostics);
        Assert.Equal(ConsulLifecycleDiagnostics.ReadinessTimeout, diagnostic.Classification);
        Assert.DoesNotContain(ConsulFixture.Secret, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disposal_failure_without_expiry_is_still_readiness_unavailable()
    {
        await using var rig = await Rig.CreateAsync(source =>
            source.DisposeFailure = new InvalidOperationException(ConsulFixture.Secret));
        await rig.StartAsync();
        await rig.WaitSampleSettledAsync();
        await rig.WaitTerminalDiagnosticRecordedAsync();

        Assert.Equal(0, rig.Client.Registers);
        var diagnostic = Assert.Single(rig.Observer.Diagnostics);
        Assert.Equal(ConsulLifecycleDiagnostics.ReadinessUnavailable, diagnostic.Classification);
        Assert.DoesNotContain(ConsulFixture.Secret, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpired_ready_and_not_ready_decisions_keep_their_existing_behaviour()
    {
        await using var ready = await Rig.CreateAsync();
        await ready.StartAsync();
        await ConsulLifecycleHarness.WaitAsync(
            () => ready.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered with an unexpired Ready decision");
        Assert.Equal(1, ready.Client.Registers);
        Assert.Empty(ready.Observer.Diagnostics);

        await using var notReady = await Rig.CreateAsync(source =>
            source.Decision = ConsulLifecycleHarness.NotReady());
        await notReady.StartAsync();
        await notReady.WaitSampleSettledAsync();
        Assert.Equal(0, notReady.Client.Registers);
        Assert.Empty(notReady.Observer.Diagnostics);
    }

    [Fact]
    public async Task A_later_expired_sample_deregisters_the_existing_registration()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.StartAsync();
        await ConsulLifecycleHarness.WaitAsync(
            () => rig.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        var registrationId = rig.Client.LastRegistrationId;

        rig.Source.Decision = ConsulLifecycleHarness.NotReady();
        rig.Source.AdvanceOnCall = Rig.Budget;
        // The second sample only starts once the poll delay is armed; advancing before that would
        // move the delay's due time instead of firing it.
        await rig.Time.WhenTimerScheduledAsync(rig.Time.GetUtcNow() + Rig.PollInterval);
        rig.Time.Advance(Rig.PollInterval);
        await ConsulLifecycleHarness.WaitAsync(
            () => rig.Client.Deregisters == 1,
            "the expired sample never deregistered");

        Assert.Equal(1, rig.Client.Registers);
        Assert.Equal(registrationId, rig.Client.LastRegistrationId);
        Assert.Contains(
            rig.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.ReadinessTimeout);
    }

    [Fact]
    public async Task A_stopping_lifecycle_outranks_the_expired_budget_and_records_nothing()
    {
        await using var rig = await Rig.CreateAsync(source => source.Hang = true);
        await rig.StartAsync();
        await ConsulLifecycleHarness.WaitAsync(
            () => rig.Source.Calls >= 1,
            "the sampler never entered the decision call");
        await rig.StopAsync();

        Assert.Equal(0, rig.Client.Registers);
        Assert.Empty(rig.Client.Operations);
        Assert.Empty(rig.Observer.Diagnostics);
    }

    /// <summary>
    /// One Consul registration lifecycle on the shared fixture and fake clock, with this file's
    /// own scoped decision source. The poll interval sits at its maximum so one settled sample
    /// is not followed by another unless the test advances the clock.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

        private Rig(
            ConsulFixture fixture,
            ConsulRegistrationLifecycle lifecycle,
            CompletingDecisionSource source,
            ConsulLifecycleHarness.ScriptedClient client,
            ManualTimeProvider time,
            ConsulLifecycleObserver observer)
        {
            Fixture = fixture;
            Lifecycle = lifecycle;
            Source = source;
            Client = client;
            Time = time;
            Observer = observer;
        }

        internal ConsulFixture Fixture { get; }

        internal ConsulRegistrationLifecycle Lifecycle { get; }

        internal CompletingDecisionSource Source { get; }

        internal ConsulLifecycleHarness.ScriptedClient Client { get; }

        internal ManualTimeProvider Time { get; }

        internal ConsulLifecycleObserver Observer { get; }

        internal static async Task<Rig> CreateAsync(
            Action<CompletingDecisionSource>? configure = null)
        {
            var time = new ManualTimeProvider();
            var source = new CompletingDecisionSource(time);
            configure?.Invoke(source);
            var client = new ConsulLifecycleHarness.ScriptedClient();
            var fixture = new ConsulFixture(configureServices: services =>
                services.AddScoped<IServiceReadinessDecisionSource>(_ => source));
            fixture.ClientFactory.CreateClient = _ => client;
            await fixture.ActivateAsync(ConsulFixture.Enabled());

            var options = new ConsulLifecycleOptions
            {
                ReadinessPollInterval = PollInterval,
                ReadinessCallBudget = Budget
            };
            var observer = new ConsulLifecycleObserver();
            var lifecycle = new ConsulRegistrationLifecycle(
                fixture.Provider,
                fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                options.Validate(),
                time,
                observer);
            return new Rig(fixture, lifecycle, source, client, time, observer);
        }

        internal Task StartAsync() => Lifecycle.StartAsync(CancellationToken.None);

        internal Task StopAsync() => Lifecycle.StopAsync(CancellationToken.None);

        /// <summary>
        /// Waits until one sample has fully settled: the call answered and the scope disposed.
        /// </summary>
        internal Task WaitSampleSettledAsync() => ConsulLifecycleHarness.WaitAsync(
            () => Source.Calls >= 1 && Source.Disposals >= 1,
            "the readiness sample never settled");

        /// <summary>
        /// Waits until exactly one terminal diagnostic was recorded for the settled sample. The
        /// poll interval keeps a second sample away unless the clock is advanced.
        /// </summary>
        internal Task WaitTerminalDiagnosticRecordedAsync() => ConsulLifecycleHarness.WaitAsync(
            () => Observer.RecordedCount == 1,
            "the settled sample never recorded its single terminal diagnostic");

        public async ValueTask DisposeAsync()
        {
            await Lifecycle.DisposeAsync();
            Fixture.Dispose();
        }
    }

    /// <summary>
    /// A scoped readiness decision source that can advance the fake clock inside the decision call
    /// or its own asynchronous disposal, return any decision, fail, or hang on the sampler token.
    /// </summary>
    private sealed class CompletingDecisionSource(
        ManualTimeProvider time) : IServiceReadinessDecisionSource, IAsyncDisposable
    {
        internal ServiceReadinessDecision? Decision { get; set; } = ConsulLifecycleHarness.Ready();

        internal Exception? Failure { get; set; }

        internal Exception? DisposeFailure { get; set; }

        internal TimeSpan? AdvanceOnCall { get; set; }

        internal TimeSpan? AdvanceOnDispose { get; set; }

        internal bool Hang { get; set; }

        internal int Calls { get; private set; }

        internal int Disposals { get; private set; }

        public async ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (AdvanceOnCall is { } delta)
            {
                time.Advance(delta);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return Decision!;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (AdvanceOnDispose is { } delta)
            {
                time.Advance(delta);
            }

            if (DisposeFailure is { } failure)
            {
                throw failure;
            }

            return ValueTask.CompletedTask;
        }
    }
}

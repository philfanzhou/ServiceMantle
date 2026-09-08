using Xunit;

namespace ServiceMantle.Consul.Tests;

using Harness = ConsulLifecycleHarness;

/// <summary>
/// Pins the contract that a <see cref="ConsulRegistrationLifecycle"/> stop which observed its
/// caller's cancellation propagates that caller's own token, and does so without abandoning the
/// remote operation it owns.
/// </summary>
/// <remarks>
/// Every boundary here is an event - the client entering a call, a timer being armed on the manual
/// clock, a gate being released - so no assertion depends on a guessed delay. The helpers this file
/// needs live in this file; the shared harness, clock, and fixture are used as they are.
/// </remarks>
public sealed class ConsulStopCancellationTests
{
    /// <summary>A value a cancelled stop must never project into its own exception.</summary>
    private const string Sentinel = "consul-stop-sentinel-do-not-project";

    /// <summary>The states a stop can be called in.</summary>
    public enum StopState
    {
        NeverStarted,
        Disabled,
        NotReadyAndAbsent,
        Registered,
        Backoff,
        InFlightRegister,
        InFlightDeregister
    }

    [Theory]
    [InlineData(StopState.NeverStarted)]
    [InlineData(StopState.Disabled)]
    [InlineData(StopState.NotReadyAndAbsent)]
    [InlineData(StopState.Registered)]
    [InlineData(StopState.Backoff)]
    [InlineData(StopState.InFlightRegister)]
    [InlineData(StopState.InFlightDeregister)]
    public async Task A_stop_cancelled_before_it_is_called_propagates_the_caller_token(StopState state)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient();
        var decisions = new Harness.ScriptedDecisionSource();
        await using var harness = await ArrangeAsync(state, client, decisions, gate);
        var registers = client.Registers;
        var deregisters = client.Deregisters;

        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var stop = harness.StopWithTokenAsync(abort.Token);
        // Whatever call is already in flight is allowed to settle; the stop still owns it.
        gate.TrySetResult();
        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => stop);

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Null(cancelled.InnerException);
        // A stop that observed the caller's cancellation starts no cleanup call of its own, and it
        // never starts a register.
        Assert.Equal(deregisters, client.Deregisters);
        Assert.Equal(registers, client.Registers);
        Assert.True(client.MaximumConcurrent <= 1, "two remote operations overlapped");
        Assert.Equal(
            state is StopState.NeverStarted or StopState.Disabled ? 0 : 1,
            client.Disposals);
    }

    [Fact]
    public async Task Cancelling_inside_the_cleanup_call_settles_it_and_starts_no_next_call()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient();
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options => options.ReadinessPollInterval = TimeSpan.FromSeconds(30));

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        client.Gate = gate;
        client.Arm();
        using var abort = new CancellationTokenSource();
        var stop = harness.StopWithTokenAsync(abort.Token);
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => stop);

        // The cooperative call observed the caller's token, settled, and no further remote call was
        // started. Its unknown outcome is not written down as remote absence.
        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Equal(["register", "deregister"], client.Operations);
        Assert.Equal(1, client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task Cancelling_inside_the_cleanup_retry_delay_starts_no_next_call()
    {
        var client = new Harness.ScriptedClient
        {
            Outcome = (kind, _) => kind == "register"
                ? ConsulClientResult.Success
                : ConsulClientResult.Unavailable,
        };
        var retryDelay = TimeSpan.FromSeconds(5);
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
                options.InitialRetryDelay = retryDelay;
                options.MaximumRetryDelay = retryDelay;
            });

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        using var abort = new CancellationTokenSource();
        var stop = harness.StopWithTokenAsync(abort.Token);
        // The retry delay is armed on the manual clock and the clock is never advanced, so only the
        // caller's cancellation can end it.
        await harness.Time
            .WhenTimerScheduledAsync(harness.Time.GetUtcNow() + retryDelay)
            .WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => stop);

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Equal(1, client.Deregisters);
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task The_caller_token_outranks_the_internal_budget_and_a_dependency_failure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient
        {
            // The call ignores its token, so it leaves with an ordinary failure rather than with
            // the cancellation, which is what makes the precedence visible.
            IgnoreCancellation = true,
            Failure = (kind, _) => kind == "deregister"
                ? new InvalidOperationException(Sentinel)
                : null,
        };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
                options.ShutdownBudget = TimeSpan.FromSeconds(1);
            });

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        client.Gate = gate;
        client.Arm();
        using var abort = new CancellationTokenSource();
        var stop = harness.StopWithTokenAsync(abort.Token);
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The internal shutdown budget expires and the caller cancels while the same attempt is
        // still in flight; the attempt then fails with an ordinary exception.
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await abort.CancelAsync();
        gate.SetResult();
        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => stop);

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Null(cancelled.InnerException);
        Assert.DoesNotContain(Sentinel, cancelled.ToString(), StringComparison.Ordinal);
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(1, client.Deregisters);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task A_non_cooperative_cleanup_call_is_settled_before_the_cancellation_is_announced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient { IgnoreCancellation = true };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options => options.ReadinessPollInterval = TimeSpan.FromSeconds(30));

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        client.Gate = gate;
        client.Arm();
        using var abort = new CancellationTokenSource();
        var stop = harness.StopWithTokenAsync(abort.Token);
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        // Every deadline the lifecycle owns has passed and the caller has cancelled, yet the call
        // still ignores its token: ownership is kept rather than bought back with a disposal or a
        // second, overlapping operation.
        harness.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.False(stop.IsCompleted, "the stop completed while the client's call was unfinished");
        Assert.False(client.Disposed, "the client was disposed while its call was unfinished");
        Assert.Equal(1, client.Deregisters);
        Assert.Equal(1, client.MaximumConcurrent);

        var samples = harness.Decisions.Calls;
        gate.SetResult();
        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(
            () => stop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        Assert.Equal(1, client.Disposals);

        // The sampler and the owner loop had already ended, so no later tick revives either of them.
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(samples, harness.Decisions.Calls);
        Assert.Equal(1, client.Registers);
        Assert.Equal(1, client.Deregisters);
    }

    [Fact]
    public async Task An_uncancelled_stop_keeps_its_existing_success_and_timeout_outcomes()
    {
        await using var succeeding = await Harness.CreateAsync();
        await succeeding.StartAsync();
        await Harness.WaitAsync(
            () => succeeding.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        await succeeding.StopWithTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConsulRemotePresence.Absent, succeeding.Lifecycle.Presence);
        Assert.Equal(1, succeeding.Client.Deregisters);

        var failing = new Harness.ScriptedClient { Outcome = (_, _) => ConsulClientResult.Unavailable };
        await using var exhausted = await Harness.CreateAsync(
            client: failing,
            configure: options =>
            {
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
                options.ShutdownBudget = TimeSpan.FromSeconds(1);
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(500);
                options.MaximumRetryDelay = TimeSpan.FromMilliseconds(500);
            });
        await exhausted.StartAsync();
        await Harness.WaitAsync(
            () => exhausted.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failed register never entered backoff");
        var stop = exhausted.StopWithTokenAsync(TestContext.Current.CancellationToken);
        await Harness.WaitAsync(() => failing.Deregisters >= 1, "the cleanup never started");
        exhausted.Time.Advance(TimeSpan.FromSeconds(2));

        // An expired internal budget with an uncancelled caller stays the internal timeout it
        // always was; it does not become a caller cancellation.
        await stop;

        Assert.Equal(ConsulRemotePresence.Unknown, exhausted.Lifecycle.Presence);
        Assert.Contains(
            exhausted.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.ShutdownTimeout);
    }

    /// <summary>Brings one lifecycle to the state its stop is supposed to be called in.</summary>
    private static async Task<ConsulLifecycleHarness> ArrangeAsync(
        StopState state,
        Harness.ScriptedClient client,
        Harness.ScriptedDecisionSource decisions,
        TaskCompletionSource gate)
    {
        var poll = TimeSpan.FromSeconds(1);
        switch (state)
        {
            case StopState.NotReadyAndAbsent:
                decisions.Current = Harness.NotReady();
                break;
            case StopState.Backoff:
                client.Outcome = (_, _) => ConsulClientResult.Unavailable;
                break;
            case StopState.InFlightRegister:
                client.Gate = gate;
                break;
        }

        var harness = await Harness.CreateAsync(
            enabled: state != StopState.Disabled,
            client: client,
            decisions: decisions,
            configure: options => options.ReadinessPollInterval = poll);
        try
        {
            if (state == StopState.NeverStarted)
            {
                return harness;
            }

            await harness.StartAsync();
            switch (state)
            {
                case StopState.Disabled:
                    break;
                case StopState.NotReadyAndAbsent:
                    await Harness.WaitAsync(
                        () => harness.Lifecycle.State == ConsulLifecycleState.NotReady,
                        "the lifecycle never settled at not ready");
                    break;
                case StopState.Backoff:
                    await Harness.WaitAsync(
                        () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
                        "the failed register never entered backoff");
                    break;
                case StopState.InFlightRegister:
                    await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
                    break;
                default:
                    await Harness.WaitAsync(
                        () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
                        "the lifecycle never registered");
                    if (state == StopState.InFlightDeregister)
                    {
                        client.Gate = gate;
                        decisions.Current = Harness.NotReady();
                        await harness.AdvanceUntilAsync(
                            poll,
                            () => client.Deregisters >= 1,
                            "the deregister never started");
                    }

                    break;
            }

            return harness;
        }
        catch (Exception)
        {
            await harness.DisposeAsync();
            throw;
        }
    }
}

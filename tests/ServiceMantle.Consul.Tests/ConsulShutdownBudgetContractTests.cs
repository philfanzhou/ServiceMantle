using Xunit;

namespace ServiceMantle.Consul.Tests;

using Harness = ConsulLifecycleHarness;

/// <summary>
/// Characterizes the shutdown budget model the lifecycle actually implements, so the documented
/// <c>max(ConsulOperationBudget, ShutdownBudget)</c> bound is pinned rather than assumed.
/// </summary>
/// <remarks>
/// The shutdown budget starts when stop begins and bounds the cleanup deregistration; an operation
/// that was already in flight keeps its own operation budget as its cancellation deadline. These
/// tests drive both budgets on the manual clock and use a gate for every rendezvous, so no assertion
/// depends on wall-clock timing. They observe the existing implementation and change none of it.
/// </remarks>
public sealed class ConsulShutdownBudgetContractTests
{
    [Fact]
    public async Task An_in_flight_deregister_outlasts_a_shorter_shutdown_budget()
    {
        var operationBudget = TimeSpan.FromSeconds(30);
        var shutdownBudget = TimeSpan.FromSeconds(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient();
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ConsulOperationBudget = operationBudget;
                options.ShutdownBudget = shutdownBudget;
            });

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        client.Arm();
        client.Gate = gate;
        harness.Decisions.Current = Harness.NotReady();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromSeconds(1),
            () => client.Deregisters == 1,
            "the deregister never started");
        Assert.Equal(ConsulLifecycleState.Deregistering, harness.Lifecycle.State);

        var stop = harness.StopAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Stopping,
            "the owner never observed the stop");

        // The whole shutdown budget passes while the in-flight attempt is still running. It is not
        // what ends that attempt, so the stop is still waiting.
        harness.Time.Advance(shutdownBudget + TimeSpan.FromSeconds(1));
        Assert.False(stop.IsCompleted, "the shutdown budget ended an attempt it does not own");

        // The attempt's own operation budget is what ends it. The gate is never released, so only a
        // deadline can settle this call.
        harness.Time.Advance(operationBudget);
        await stop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(["register", "deregister"], client.Operations);
        Assert.Equal(1, client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Contains(
            harness.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.DeregisterTimeout);
        Assert.Contains(
            harness.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.ShutdownTimeout);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task A_cleanup_retry_uses_only_what_is_left_of_the_budget_stop_started()
    {
        var shutdownBudget = TimeSpan.FromSeconds(10);
        var settleAfter = TimeSpan.FromSeconds(6);
        var retryDelay = TimeSpan.FromSeconds(5);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient
        {
            // The register ignores its cancellation, so the test decides how much of the shutdown
            // budget the in-flight attempt consumes before it settles.
            IgnoreCancellation = true,
            Outcome = (kind, _) => kind == "register"
                ? ConsulClientResult.Success
                : ConsulClientResult.Unavailable,
            Gate = gate,
        };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
                options.ConsulOperationBudget = TimeSpan.FromSeconds(30);
                options.ShutdownBudget = shutdownBudget;
                options.InitialRetryDelay = retryDelay;
                options.MaximumRetryDelay = retryDelay;
            });

        await harness.StartAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = harness.StopAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Stopping,
            "the owner never observed the stop");

        harness.Time.Advance(settleAfter);
        Assert.False(stop.IsCompleted, "the stop completed while the register was unfinished");
        gate.SetResult();
        await Harness.WaitAsync(() => client.Deregisters == 1, "the cleanup deregister never ran");

        // The cleanup's retry delay is armed with the full retry delay, which would run past the
        // budget; only the remaining budget may end the wait.
        await harness.Time
            .WhenTimerScheduledAsync(harness.Time.GetUtcNow() + retryDelay)
            .WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(shutdownBudget - settleAfter);
        await stop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // A budget restarted when the cleanup began would still have had time left here and would
        // have issued a second deregister.
        Assert.Equal(1, client.Deregisters);
        Assert.Equal(1, client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Contains(
            harness.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.ShutdownTimeout);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task A_cleanup_that_succeeds_inside_the_budget_is_one_call_released_after_it_settles()
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
        client.Arm();
        client.Gate = gate;
        var stop = harness.StopAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The session outlives the call it owns: nothing is disposed while the deregister runs.
        Assert.False(client.Disposed, "the session was released before its call settled");
        gate.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(["register", "deregister"], client.Operations);
        Assert.Equal(1, client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.Equal(1, client.Disposals);
    }
}

using ServiceMantle.Consul;
using Xunit;

namespace ServiceMantle.Consul.Tests;

using Harness = ConsulLifecycleHarness;

/// <summary>
/// The stop/dispose interlock (#565): the lifecycle is both a hosted service and an async
/// disposable, and the host's stop and the container's disposal can interleave. Every interleaving
/// must keep the serialized-stop guarantees — exactly one cleanup deregistration, exactly one
/// session disposal, and no exception escaping as a NullReferenceException or an
/// ObjectDisposedException.
/// </summary>
public sealed class ConsulStopDisposeInterlockTests
{
    [Fact]
    public async Task Concurrent_stops_deregister_and_dispose_exactly_once()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered, "registered");

        // Two stop entries racing each other, the shape a test-host teardown produces.
        var first = harness.StopAsync();
        var second = harness.StopAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, harness.Client.Deregisters);
        Assert.Equal(1, harness.Client.Disposals);
        Assert.Equal(ConsulLifecycleState.Stopping, harness.Lifecycle.State);
    }

    [Fact]
    public async Task Disposal_while_a_stops_cleanup_is_in_flight_waits_for_it()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered, "registered");

        // Park the cleanup deregistration mid-flight, then dispose: the disposal must await the
        // stop instead of releasing the session underneath the very operation it is waiting on.
        harness.Client.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = harness.StopAsync();
        await harness.Client.Entered.Task;
        var disposal = harness.Lifecycle.DisposeAsync().AsTask();

        Assert.Equal(0, harness.Client.Deregisters);
        Assert.Equal(0, harness.Client.Disposals);

        harness.Client.Gate.TrySetResult();
        await Task.WhenAll(stop, disposal);

        Assert.Equal(1, harness.Client.Deregisters);
        Assert.Equal(1, harness.Client.Disposals);
    }

    [Fact]
    public async Task Disposal_before_any_stop_releases_without_deregistering_and_stop_returns_safely()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered, "registered");

        // A host disposed without a stop keeps its registration: disposal never deregisters.
        await harness.Lifecycle.DisposeAsync();
        Assert.Equal(0, harness.Client.Deregisters);
        Assert.Equal(1, harness.Client.Disposals);

        // The late stop entry joins the finished release instead of touching released primitives.
        await harness.Lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(0, harness.Client.Deregisters);
        Assert.Equal(1, harness.Client.Disposals);
    }

    [Fact]
    public async Task A_disabled_lifecycle_tolerates_dispose_and_stop_in_either_order()
    {
        await using var harness = await Harness.CreateAsync(enabled: false);
        await harness.StartAsync();

        var stop = harness.StopAsync();
        var disposal = harness.Lifecycle.DisposeAsync().AsTask();
        await Task.WhenAll(stop, disposal);

        Assert.Equal(ConsulLifecycleState.Disabled, harness.Lifecycle.State);
        Assert.Equal(0, harness.Client.Disposals);
    }
}

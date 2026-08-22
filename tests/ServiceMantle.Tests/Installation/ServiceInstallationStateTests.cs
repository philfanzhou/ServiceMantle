using ServiceMantle;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Installation;

public sealed class ServiceInstallationStateTests
{
    [Fact]
    public void CreatePending_creates_an_incomplete_state()
    {
        var serviceId = ServiceId.Parse("signacore");
        var state = ServiceInstallationState.CreatePending(serviceId);

        Assert.Equal(serviceId, state.ServiceId);
        Assert.Equal(InstallationStatus.PendingSetup, state.Status);
        Assert.False(state.IsCompleted);
    }

    [Fact]
    public void CreatePending_rejects_a_null_service_id()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceInstallationState.CreatePending(null!));
    }

    [Fact]
    public void Complete_returns_a_new_completed_state_without_mutating_the_pending_state()
    {
        var pending = ServiceInstallationState.CreatePending(ServiceId.Parse("signacore"));

        var completed = pending.Complete();

        Assert.NotSame(pending, completed);
        Assert.Equal(InstallationStatus.PendingSetup, pending.Status);
        Assert.False(pending.IsCompleted);
        Assert.Equal(InstallationStatus.Completed, completed.Status);
        Assert.True(completed.IsCompleted);
        Assert.Equal(pending.ServiceId, completed.ServiceId);
    }

    [Fact]
    public void Complete_rejects_an_already_completed_state()
    {
        var completed = ServiceInstallationState.CreatePending(ServiceId.Parse("signacore")).Complete();

        Assert.Throws<InvalidOperationException>(() => completed.Complete());
    }
}

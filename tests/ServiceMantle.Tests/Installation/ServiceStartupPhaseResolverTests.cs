using ServiceMantle;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Installation;

public sealed class ServiceStartupPhaseResolverTests
{
    private static readonly ServiceId ServiceId = ServiceId.Parse("signacore");

    [Fact]
    public void Resolve_returns_bootstrap_configuration_when_bootstrap_is_missing()
    {
        var phase = ServiceStartupPhaseResolver.Resolve(false, null);

        Assert.Equal(ServiceStartupPhase.BootstrapConfiguration, phase);
    }

    [Fact]
    public void Resolve_ignores_installation_state_when_bootstrap_is_missing()
    {
        var completed = ServiceInstallationState.CreatePending(ServiceId).Complete();

        var phase = ServiceStartupPhaseResolver.Resolve(false, completed);

        Assert.Equal(ServiceStartupPhase.BootstrapConfiguration, phase);
    }

    [Fact]
    public void Resolve_returns_pending_setup_when_bootstrap_exists_without_state()
    {
        var phase = ServiceStartupPhaseResolver.Resolve(true, null);

        Assert.Equal(ServiceStartupPhase.PendingSetup, phase);
    }

    [Fact]
    public void Resolve_returns_pending_setup_for_pending_installation()
    {
        var pending = ServiceInstallationState.CreatePending(ServiceId);

        var phase = ServiceStartupPhaseResolver.Resolve(true, pending);

        Assert.Equal(ServiceStartupPhase.PendingSetup, phase);
    }

    [Fact]
    public void Resolve_returns_completed_for_completed_installation()
    {
        var completed = ServiceInstallationState.CreatePending(ServiceId).Complete();

        var phase = ServiceStartupPhaseResolver.Resolve(true, completed);

        Assert.Equal(ServiceStartupPhase.Completed, phase);
    }
}

using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceStartupPhaseResolverTests
{
    [Fact]
    public void Resolver_preserves_core_phase_semantics()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IServiceStartupPhaseResolver>();
        var pending = ServiceInstallationState.CreatePending(ServiceId.Parse("catalog"));

        Assert.Equal(
            ServiceStartupPhase.BootstrapConfiguration,
            resolver.Resolve(hasBootstrapConfiguration: false, pending));
        Assert.Equal(
            ServiceStartupPhase.PendingSetup,
            resolver.Resolve(hasBootstrapConfiguration: true, pending));
        Assert.Equal(
            ServiceStartupPhase.Completed,
            resolver.Resolve(hasBootstrapConfiguration: true, pending.Complete()));
    }
}

using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore;

internal sealed class DefaultServiceStartupPhaseResolver : IServiceStartupPhaseResolver
{
    public ServiceStartupPhase Resolve(
        bool hasBootstrapConfiguration,
        ServiceInstallationState? installationState) =>
        ServiceStartupPhaseResolver.Resolve(hasBootstrapConfiguration, installationState);
}

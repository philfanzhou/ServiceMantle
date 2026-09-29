using ServiceMantle.Installation;

namespace ServiceMantle.Web;

internal sealed class DefaultServiceStartupPhaseResolver : IServiceStartupPhaseResolver
{
    public ServiceStartupPhase Resolve(
        bool hasBootstrapConfiguration,
        ServiceInstallationState? installationState) =>
        ServiceStartupPhaseResolver.Resolve(hasBootstrapConfiguration, installationState);
}

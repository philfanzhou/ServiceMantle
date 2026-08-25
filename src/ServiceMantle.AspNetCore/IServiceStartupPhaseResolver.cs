using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Resolves the core startup phase for host-level gates without coupling the core package to ASP.NET Core.
/// </summary>
public interface IServiceStartupPhaseResolver
{
    /// <summary>
    /// Resolves the startup phase from bootstrap availability and durable installation state.
    /// </summary>
    ServiceStartupPhase Resolve(
        bool hasBootstrapConfiguration,
        ServiceInstallationState? installationState);
}

internal sealed class DefaultServiceStartupPhaseResolver : IServiceStartupPhaseResolver
{
    public ServiceStartupPhase Resolve(
        bool hasBootstrapConfiguration,
        ServiceInstallationState? installationState) =>
        ServiceStartupPhaseResolver.Resolve(hasBootstrapConfiguration, installationState);
}

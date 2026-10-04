using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicit MariaDb deployment capability registration.</summary>
public static class ServiceMantleMariaDbDeploymentCapabilityServiceCollectionExtensions
{
    /// <summary>Registers only deployment support, without bootstrap, preparation or migration locking.</summary>
    public static IServiceCollection AddServiceMantleMariaDbDeploymentCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            MariaDbDatabaseDeploymentCapabilityProvider>());
        return services;
    }
}

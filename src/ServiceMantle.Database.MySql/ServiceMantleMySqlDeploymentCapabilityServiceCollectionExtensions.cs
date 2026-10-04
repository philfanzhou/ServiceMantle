using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicit MySql deployment capability registration.</summary>
public static class ServiceMantleMySqlDeploymentCapabilityServiceCollectionExtensions
{
    /// <summary>Registers only deployment support, without bootstrap, preparation or migration locking.</summary>
    public static IServiceCollection AddServiceMantleMySqlDeploymentCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            MySqlDatabaseDeploymentCapabilityProvider>());
        return services;
    }
}

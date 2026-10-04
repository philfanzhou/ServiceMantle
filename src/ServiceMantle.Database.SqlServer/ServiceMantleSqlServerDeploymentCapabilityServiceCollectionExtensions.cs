using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicit SQL Server deployment capability registration.</summary>
public static class ServiceMantleSqlServerDeploymentCapabilityServiceCollectionExtensions
{
    /// <summary>Registers only deployment support, without bootstrap, preparation or migration locking.</summary>
    public static IServiceCollection AddServiceMantleSqlServerDeploymentCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            SqlServerDatabaseDeploymentCapabilityProvider>());
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicit PostgreSQL deployment capability registration.</summary>
public static class ServiceMantlePostgreSqlDeploymentCapabilityServiceCollectionExtensions
{
    /// <summary>Registers only deployment support, without bootstrap, preparation or migration locking.</summary>
    public static IServiceCollection AddServiceMantlePostgreSqlDeploymentCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            PostgreSqlDatabaseDeploymentCapabilityProvider>());
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicit Oracle deployment capability registration.</summary>
public static class ServiceMantleOracleDeploymentCapabilityServiceCollectionExtensions
{
    /// <summary>Registers only deployment support. Built-in single-instance identity remains unavailable.</summary>
    public static IServiceCollection AddServiceMantleOracleDeploymentCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            OracleDatabaseDeploymentCapabilityProvider>());
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Core-only registration for a caller-driven startup database gate.</summary>
public static class ServiceMantleStartupDatabaseGateServiceCollectionExtensions
{
    /// <summary>Registers the gate, receipt and shared registries without options, service identity,
    /// configuration, executor resolution, I/O or a hosted entry. Registration is idempotent.</summary>
    public static IServiceCollection AddServiceMantleStartupDatabaseGateServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BootstrapDatabaseProviderRegistry>(provider => new(
            provider.GetServices<IBootstrapDatabaseProvider>()));
        services.TryAddSingleton<DatabaseDeploymentCapabilityRegistry>(provider => new(
            provider.GetServices<IDatabaseDeploymentCapabilityProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.TryAddSingleton<DatabaseTargetPreparationProviderRegistry>(provider => new(
            provider.GetServices<IDatabaseTargetPreparationProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.TryAddSingleton<DatabaseMigrationLockProviderRegistry>(provider => new(
            provider.GetServices<IDatabaseMigrationLockProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.TryAddSingleton<StartupDatabaseReceipt>();
        services.TryAddSingleton<StartupDatabaseGate>();
        return services;
    }
}

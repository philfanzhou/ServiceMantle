using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle;
using ServiceMantle.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers provider-independent typed service-setting snapshots.</summary>
public static class ServiceSettingSnapshotServiceCollectionExtensions
{
    /// <summary>
    /// Registers the immutable definition registry, snapshot source adapter, current accessor,
    /// and serialized loader. Consumers provide <see cref="IServiceSettingStore"/> and register
    /// <see cref="IServiceSettingRootKeySource"/> when the catalog contains sensitive settings.
    /// </summary>
    public static IServiceCollection AddServiceMantleSettingSnapshots(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(serviceProvider => new ServiceSettingDefinitionRegistry(
            serviceProvider.GetServices<IServiceSettingDefinitionProvider>(),
            serviceProvider.GetServices<IServiceSettingCompositeValidator>()));
        services.TryAddSingleton<ServiceSettingCurrentSnapshotAccessor>();
        services.TryAddSingleton<IServiceSettingCurrentSnapshotAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<ServiceSettingCurrentSnapshotAccessor>());
        services.TryAddSingleton<IServiceSettingSnapshotSource, ServiceSettingStoreSnapshotSource>();
        services.TryAddSingleton(serviceProvider => new ServiceSettingSnapshotLoader(
            serviceProvider.GetRequiredService<ServiceId>(),
            serviceProvider.GetRequiredService<IServiceSettingSnapshotSource>(),
            serviceProvider.GetRequiredService<ServiceSettingDefinitionRegistry>(),
            serviceProvider.GetRequiredService<ServiceSettingCurrentSnapshotAccessor>(),
            serviceProvider.GetService<IServiceSettingRootKeySource>()));
        return services;
    }
}

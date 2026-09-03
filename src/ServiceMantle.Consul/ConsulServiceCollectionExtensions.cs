using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the optional Consul catalog and explicit client boundary.</summary>
public static class ConsulServiceCollectionExtensions
{
    /// <summary>
    /// Registers definitions, validation, a deferred replaceable factory and a session provider.
    /// Consumers provide ServiceId, InstanceId and an activated setting snapshot accessor.
    /// No hosted service, network I/O or client is created by registration or provider resolution.
    /// </summary>
    public static IServiceCollection AddServiceMantleConsul(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingDefinitionProvider, ConsulSettingDefinitions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingCompositeValidator, ConsulSettingDefinitions>());
        services.TryAddSingleton<IConsulClientFactory, ConsulHttpClientFactory>();
        services.TryAddSingleton(provider => new ConsulClientProvider(
            provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>(),
            provider.GetRequiredService<ServiceId>(), provider.GetRequiredService<InstanceId>(),
            () => provider.GetRequiredService<IConsulClientFactory>()));
        return services;
    }
}

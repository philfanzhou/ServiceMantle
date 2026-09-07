using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the optional Consul catalog, explicit client boundary, and lifecycle.</summary>
public static class ConsulServiceCollectionExtensions
{
    /// <summary>
    /// Registers definitions, validation, a deferred replaceable factory, a session provider, and
    /// the registration lifecycle with its default timing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Consumers provide ServiceId, InstanceId, an activated setting snapshot accessor, and the
    /// shared <c>IServiceReadinessDecisionSource</c>. Registration creates no client, timer, sampler,
    /// background loop, or network request; the lifecycle takes ownership only after the host starts.
    /// </remarks>
    public static IServiceCollection AddServiceMantleConsul(this IServiceCollection services) =>
        services.AddServiceMantleConsul(configure: null);

    /// <summary>
    /// Registers the same capability with explicit lifecycle timing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the validated lifecycle timing.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Every timing value is validated here, before the host is built and therefore before any
    /// readiness sampler, timer, or remote operation can exist. Repeating this call is idempotent,
    /// but two calls that configure different timing fail as a conflicting configuration.
    /// </remarks>
    /// <exception cref="ConsulConfigurationException">
    /// A timing value is out of range, or a repeated registration configures different timing.
    /// </exception>
    public static IServiceCollection AddServiceMantleConsul(
        this IServiceCollection services,
        Action<ConsulLifecycleOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ConsulLifecycleOptions();
        configure?.Invoke(options);
        // Validation happens at registration time, so an invalid value can never reach a timer.
        var settings = options.Validate();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingDefinitionProvider, ConsulSettingDefinitions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingCompositeValidator, ConsulSettingDefinitions>());
        services.TryAddSingleton<IConsulClientFactory, ConsulHttpClientFactory>();
        services.TryAddSingleton(provider => new ConsulClientProvider(
            provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>(),
            provider.GetRequiredService<ServiceId>(), provider.GetRequiredService<InstanceId>(),
            () => provider.GetRequiredService<IConsulClientFactory>()));
        services.AddSingleton(settings);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ConsulRegistrationLifecycle)))
        {
            return services;
        }

        services.AddSingleton<ConsulLifecycleObserver>();
        services.AddSingleton(provider => new ConsulRegistrationLifecycle(
            provider.GetRequiredService<ConsulClientProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Single(provider.GetServices<ConsulLifecycleSettings>()),
            TimeProvider.System,
            provider.GetRequiredService<ConsulLifecycleObserver>()));
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ConsulRegistrationLifecycle>());
        return services;
    }

    /// <summary>Accepts repeated registrations only while they agree on the timing.</summary>
    private static ConsulLifecycleSettings Single(IEnumerable<ConsulLifecycleSettings> registered)
    {
        ConsulLifecycleSettings? baseline = null;
        foreach (var settings in registered)
        {
            if (baseline is not null && baseline != settings)
            {
                throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
            }

            baseline = settings;
        }

        return baseline ?? throw new ConsulConfigurationException(
            ConsulConfigurationError.InvalidConfiguration);
    }
}

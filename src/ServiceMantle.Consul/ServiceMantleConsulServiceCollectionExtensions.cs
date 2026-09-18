using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using ServiceMantle.Discovery;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the optional Consul catalog, explicit client boundary, and lifecycle.</summary>
public static class ServiceMantleConsulServiceCollectionExtensions
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
    /// Every timing value is validated here, before any descriptor that could reach a timer is
    /// written and therefore before any readiness sampler, timer, or remote operation can exist.
    /// Repeating this call is idempotent, but two calls that configure different timing are a
    /// conflicting configuration: that conflict is detected when the lifecycle is first resolved,
    /// before any of its background work starts - not by this method and not necessarily by
    /// building a service provider or host.
    /// </remarks>
    /// <exception cref="ConsulConfigurationException">A timing value is out of range.</exception>
    public static IServiceCollection AddServiceMantleConsul(
        this IServiceCollection services,
        Action<ServiceRegistrationLifecycleOptions>? configure) =>
        AddCore(services, configure, advertisementConfiguration: null);

    /// <summary>
    /// Registers the same capability with explicit lifecycle timing and this process's explicit
    /// per-instance advertised address and port.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureLifecycle">Configures the validated lifecycle timing.</param>
    /// <param name="configureAdvertisement">
    /// Configures the instance-level advertisement. Supplying both the address and the port makes
    /// this process advertise its own endpoint instead of the service-level values shared by every
    /// instance; leaving both null keeps the service-level behaviour byte for byte.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// <para>
    /// The advertisement values are validated here with exactly the rules the service-level
    /// setting values obey - the address is an IP literal or a 1-253 character DNS name over
    /// ASCII letters, digits, dots, and hyphens, and the port is 1-65535 - and the address and
    /// the port must be supplied together. An invalid or half-supplied advertisement fails this
    /// call itself with <see cref="ConsulConfigurationException"/>, before any descriptor that
    /// could reach a registration is written.
    /// </para>
    /// <para>
    /// The captured values are fixed for the process lifetime: they are captured into every
    /// client session together with the snapshot and never hot-reloaded. Repeating the
    /// registration is idempotent while the values agree; two calls that disagree - including one
    /// configured and one unconfigured - are a conflicting configuration detected when the
    /// lifecycle is first resolved, before any background work starts.
    /// </para>
    /// <para>
    /// The service-level combination validation is unchanged: an enabled snapshot still requires
    /// a valid service-level address and port regardless of the instance-level advertisement,
    /// because the setting validity must not depend on whether this process configured one. The
    /// instance-level address and port never appear in diagnostics, exception messages, or
    /// <c>ToString</c> output.
    /// </para>
    /// </remarks>
    /// <exception cref="ConsulConfigurationException">
    /// A timing or advertisement value is out of range, or only one of the address and the port
    /// was supplied.</exception>
    public static IServiceCollection AddServiceMantleConsul(
        this IServiceCollection services,
        Action<ServiceRegistrationLifecycleOptions>? configureLifecycle,
        Action<ServiceInstanceAdvertisementOptions>? configureAdvertisement)
    {
        ArgumentNullException.ThrowIfNull(configureAdvertisement);
        var advertisementOptions = new ServiceInstanceAdvertisementOptions();
        configureAdvertisement(advertisementOptions);
        // Validation happens at registration time, so an invalid value can never reach a session.
        var advertisement = ConsulInstanceAdvertisement.FromOptions(advertisementOptions);
        return AddCore(services, configureLifecycle, advertisement);
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        Action<ServiceRegistrationLifecycleOptions>? configure,
        ConsulInstanceAdvertisement? advertisementConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ServiceRegistrationLifecycleOptions();
        configure?.Invoke(options);
        // Validation happens at registration time, so an invalid value can never reach a timer.
        var settings = ConsulLifecycleSettings.FromOptions(options);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingDefinitionProvider, ConsulSettingDefinitions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingCompositeValidator, ConsulSettingDefinitions>());
        services.TryAddSingleton<IConsulClientFactory, ConsulHttpClientFactory>();
        services.TryAddSingleton(provider => new ConsulClientProvider(
            provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>(),
            provider.GetRequiredService<ServiceId>(), provider.GetRequiredService<InstanceId>(),
            () => provider.GetRequiredService<IConsulClientFactory>(),
            ConsulClientProvider.SingleAdvertisement(provider.GetServices<ConsulInstanceAdvertisement>())));
        services.AddSingleton(settings);
        if (advertisementConfiguration is not null)
        {
            services.AddSingleton(advertisementConfiguration);
        }

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

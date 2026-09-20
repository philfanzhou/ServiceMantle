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
        AddCore(services, configure, advertisementConfiguration: null, snapshotAccessorType: null);

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
        return AddCore(services, configureLifecycle, advertisement, snapshotAccessorType: null);
    }

    /// <summary>
    /// Registers the same Consul registration capability reading its captured setting snapshot
    /// from one dedicated accessor type instead of the process-global snapshot source, keeping the
    /// global setting catalog and accessor untouched.
    /// </summary>
    /// <typeparam name="TSnapshotAccessor">
    /// The caller-owned singleton accessor type that publishes the complete Consul
    /// <c>discovery.*</c> snapshot for this process.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureLifecycle">Configures the validated lifecycle timing.</param>
    /// <param name="configureAdvertisement">
    /// Configures the instance-level advertisement; null leaves it unconfigured, exactly like the
    /// non-generic entries.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// <para>
    /// This entry registers the Consul client boundary and lifecycle only: it does not register
    /// <c>IServiceSettingDefinitionProvider</c> or <c>IServiceSettingCompositeValidator</c> for the
    /// global catalog, and it does not replace the global accessor, store, loader, query, or update
    /// descriptors. A consumer that projects a dedicated discovery snapshot keeps its product
    /// setting authority exactly as it is.
    /// </para>
    /// <para>
    /// The caller registers <typeparamref name="TSnapshotAccessor"/> as a singleton and activates
    /// the complete snapshot for the same <c>ServiceId</c> before the host starts. This library
    /// creates no storage for it, reads no <c>IConfiguration</c>, migrates no keys, and performs no
    /// validation or protection of its own: schema, token sensitivity, service identity, and the
    /// enabled value are checked at the same consumer boundary as before.
    /// </para>
    /// <para>
    /// The typed accessor is resolved and read only inside <c>ConsulClientProvider.CreateClient</c>'s
    /// existing safe capture boundary - registration, provider construction, and host builds never
    /// resolve it and never create a client. A registration that is missing, throws while being
    /// resolved, or yields no usable snapshot fails with the closed
    /// <see cref="ConsulConfigurationError.SnapshotUnavailable"/> category carrying no inner
    /// exception; an invalid snapshot keeps <see cref="ConsulConfigurationError.InvalidConfiguration"/>.
    /// </para>
    /// <para>
    /// All entries converge on one lifecycle owner. Repeating the typed entry with the same
    /// accessor type and agreeing timing and advertisement values is idempotent; mixing it with a
    /// non-generic entry, naming a different accessor type, or disagreeing on timing or
    /// advertisement is a conflicting configuration rejected with
    /// <see cref="ConsulConfigurationError.InvalidConfiguration"/> when the lifecycle is first
    /// resolved, before any background work starts - not first-wins or last-wins, and not
    /// necessarily when a service provider or host is built.
    /// </para>
    /// </remarks>
    /// <exception cref="ConsulConfigurationException">
    /// A timing or advertisement value is out of range, or only one of the address and the port
    /// was supplied.</exception>
    public static IServiceCollection AddServiceMantleConsul<TSnapshotAccessor>(
        this IServiceCollection services,
        Action<ServiceRegistrationLifecycleOptions>? configureLifecycle = null,
        Action<ServiceInstanceAdvertisementOptions>? configureAdvertisement = null)
        where TSnapshotAccessor : class, IServiceSettingCurrentSnapshotAccessor
    {
        ConsulInstanceAdvertisement? advertisement = null;
        if (configureAdvertisement is not null)
        {
            var advertisementOptions = new ServiceInstanceAdvertisementOptions();
            configureAdvertisement(advertisementOptions);
            // Validation happens at registration time, so an invalid value can never reach a session.
            advertisement = ConsulInstanceAdvertisement.FromOptions(advertisementOptions);
        }

        return AddCore(services, configureLifecycle, advertisement, typeof(TSnapshotAccessor));
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        Action<ServiceRegistrationLifecycleOptions>? configure,
        ConsulInstanceAdvertisement? advertisementConfiguration,
        Type? snapshotAccessorType)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ServiceRegistrationLifecycleOptions();
        configure?.Invoke(options);
        // Validation happens at registration time, so an invalid value can never reach a timer.
        var settings = ConsulLifecycleSettings.FromOptions(options);

        // Only the non-generic entries contribute the Consul catalog to the global setting
        // registry; the typed entry isolates the global definitions, validators, and accessor.
        if (snapshotAccessorType is null)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingDefinitionProvider, ConsulSettingDefinitions>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceSettingCompositeValidator, ConsulSettingDefinitions>());
        }

        services.TryAddSingleton<IConsulClientFactory, ConsulHttpClientFactory>();
        services.TryAddSingleton(provider => new ConsulClientProvider(
            ConsulSnapshotSourceSelection.ResolveAccessor(provider),
            provider.GetRequiredService<ServiceId>(), provider.GetRequiredService<InstanceId>(),
            () => provider.GetRequiredService<IConsulClientFactory>(),
            ConsulClientProvider.SingleAdvertisement(provider.GetServices<ConsulInstanceAdvertisement>())));
        // Each registration records its snapshot-source choice; disagreement is detected when the
        // lifecycle is first resolved, exactly like the timing and advertisement copies below.
        services.AddSingleton(new ConsulSnapshotSourceSelection(snapshotAccessorType));
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

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.Bootstrap;
using ServiceMantle.Http;
using ServiceMantle.Logging;
using ServiceMantle.Migration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the provider-independent ServiceMantle hosting foundation.
/// </summary>
public static class ServiceMantleServiceCollectionExtensions
{
    /// <summary>
    /// Registers service identity, startup-phase resolution, local Bootstrap management,
    /// and provider-independent database capability registries.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceId">The stable identity shared by all service instances.</param>
    /// <param name="instanceId">The identity of this running instance.</param>
    /// <param name="bootstrapFilePath">An optional explicit local Bootstrap file path.</param>
    /// <param name="serviceVersion">
    /// An optional explicit service version. When omitted, the entry assembly version is used.
    /// </param>
    /// <returns>A builder used to add optional providers and a migration executor.</returns>
    public static ServiceMantleBuilder AddServiceMantle(
        this IServiceCollection services,
        ServiceId serviceId,
        InstanceId instanceId,
        string? bootstrapFilePath = null,
        string? serviceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(instanceId);

        // The store is created lazily so that its provider-id resolver snapshot contains every
        // provider registered on the returned builder, not only the ones registered before this
        // call. Only the path is needed eagerly, for the duplicate-registration check below.
        var resolvedBootstrapFilePath = BootstrapFileStore.ResolveFilePath(serviceId, bootstrapFilePath);
        var resolvedServiceVersion = ServiceLogContext.ResolveServiceVersion(serviceVersion);
        var existingRegistration = services
            .Where(descriptor => descriptor.ServiceType == typeof(ServiceMantleRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as ServiceMantleRegistration)
            .SingleOrDefault(registration => registration is not null);

        if (existingRegistration is not null)
        {
            if (existingRegistration.ServiceId != serviceId ||
                existingRegistration.InstanceId != instanceId ||
                !string.Equals(
                    existingRegistration.ServiceVersion,
                    resolvedServiceVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existingRegistration.BootstrapFilePath,
                    resolvedBootstrapFilePath,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ServiceMantle is already registered with different host identity or Bootstrap settings.");
            }

            return new ServiceMantleBuilder(services);
        }

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(ServiceId) ||
                descriptor.ServiceType == typeof(InstanceId) ||
                descriptor.ServiceType == typeof(BootstrapFileStore) ||
                descriptor.ServiceType == typeof(ServiceLogContext)))
        {
            throw new InvalidOperationException(
                "ServiceMantle host-owned identity and Bootstrap services must be registered through AddServiceMantle.");
        }

        services.AddSingleton(new ServiceMantleRegistration(
            serviceId,
            instanceId,
            resolvedBootstrapFilePath,
            resolvedServiceVersion));
        services.AddSingleton(serviceId);
        services.AddSingleton(instanceId);
        services.AddSingleton(new ServiceLogContext(serviceId, instanceId, resolvedServiceVersion));
        services.TryAddSingleton<BootstrapDatabaseProviderRegistry>(serviceProvider =>
            new BootstrapDatabaseProviderRegistry(
                serviceProvider.GetServices<IBootstrapDatabaseProvider>()));
        services.AddSingleton(serviceProvider => new BootstrapFileStore(
            serviceId,
            serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>(),
            resolvedBootstrapFilePath));
        services.TryAddSingleton<IBootstrapCandidateValidator, BootstrapDatabaseCandidateValidator>();
        services.TryAddSingleton<BootstrapConfigurationManager>();
        services.TryAddSingleton<DatabaseTargetPreparationProviderRegistry>(serviceProvider =>
            new DatabaseTargetPreparationProviderRegistry(
                serviceProvider.GetServices<IDatabaseTargetPreparationProvider>(),
                serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.TryAddSingleton<DatabaseMigrationLockProviderRegistry>(serviceProvider =>
            new DatabaseMigrationLockProviderRegistry(
                serviceProvider.GetServices<IDatabaseMigrationLockProvider>(),
                serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.TryAddSingleton<IServiceStartupPhaseResolver, DefaultServiceStartupPhaseResolver>();
        services.TryAddSingleton<ServiceMantleExceptionMappingRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantleProblemDetailsStartupValidator>());

        return new ServiceMantleBuilder(services);
    }

    /// <summary>
    /// Adds a provider-specific Bootstrap validator without introducing a driver dependency
    /// into ServiceMantle.AspNetCore.
    /// </summary>
    public static ServiceMantleBuilder AddBootstrapDatabaseProvider<TProvider>(
        this ServiceMantleBuilder builder)
        where TProvider : class, IBootstrapDatabaseProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBootstrapDatabaseProvider, TProvider>());
        return builder;
    }

    /// <summary>
    /// Adds a provider-specific database target preparation capability without introducing a
    /// driver dependency into ServiceMantle.AspNetCore.
    /// </summary>
    public static ServiceMantleBuilder AddDatabaseTargetPreparationProvider<TProvider>(
        this ServiceMantleBuilder builder)
        where TProvider : class, IDatabaseTargetPreparationProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDatabaseTargetPreparationProvider, TProvider>());
        return builder;
    }

    /// <summary>
    /// Adds a provider-specific migration lock without introducing a driver dependency
    /// into ServiceMantle.AspNetCore.
    /// </summary>
    public static ServiceMantleBuilder AddMigrationLockProvider<TProvider>(
        this ServiceMantleBuilder builder)
        where TProvider : class, IDatabaseMigrationLockProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDatabaseMigrationLockProvider, TProvider>());
        return builder;
    }

    /// <summary>
    /// Adds the consuming service's scoped migration executor and orchestrator.
    /// </summary>
    public static ServiceMantleBuilder AddDatabaseMigration<TExecutor>(
        this ServiceMantleBuilder builder)
        where TExecutor : class, IDatabaseMigrationExecutor
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existingExecutor = builder.Services
            .SingleOrDefault(descriptor => descriptor.ServiceType == typeof(IDatabaseMigrationExecutor));

        if (existingExecutor is not null && existingExecutor.ImplementationType != typeof(TExecutor))
        {
            throw new InvalidOperationException(
                "A different ServiceMantle database migration executor is already registered.");
        }

        builder.Services.TryAddScoped<IDatabaseMigrationExecutor, TExecutor>();
        builder.Services.TryAddScoped<DatabaseMigrationOrchestrator>();
        return builder;
    }

    /// <summary>
    /// Adds an exact exception-type mapping to the ServiceMantle Problem Details table.
    /// </summary>
    /// <typeparam name="TException">The exact exception type handled by the mapping.</typeparam>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="statusCode">The fixed HTTP error status returned by this mapping.</param>
    /// <param name="errorCode">The stable error code used in the response and type URI.</param>
    /// <param name="title">The fixed, public-safe Problem Details title.</param>
    /// <param name="extensionFields">
    /// Optional explicitly named extension value factories. The names form the mapping's whitelist;
    /// values are supplied by the consuming service and are not sanitized by ServiceMantle.
    /// </param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// Registrations are validated when the host starts. Repeating an identical registration is
    /// idempotent. A second registration for the same exception type with a different status, code,
    /// title, extension whitelist, or value factory is a startup error. Mappings are exact-type:
    /// derived exception types must be registered separately or they use the fail-closed fallback.
    /// </remarks>
    public static ServiceMantleBuilder AddExceptionMapping<TException>(
        this ServiceMantleBuilder builder,
        int statusCode,
        string errorCode,
        string title,
        IReadOnlyDictionary<string, Func<TException, object?>>? extensionFields = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IServiceMantleExceptionMappingRegistration>(
            new ServiceMantleExceptionMappingRegistration<TException>(
                statusCode,
                errorCode,
                title,
                extensionFields));
        return builder;
    }

    /// <summary>Adds the mandatory security response-header capability.</summary>
    /// <remarks>This opt-in registration is idempotent and exposes no weakening options.</remarks>
    public static ServiceMantleBuilder AddSecurityResponseHeaders(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ServiceMantleSecurityResponseHeadersRegistration>();
        return builder;
    }
}

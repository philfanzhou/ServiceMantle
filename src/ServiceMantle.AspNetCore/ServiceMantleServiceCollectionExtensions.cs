using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.Logging;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.RateLimiting;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Logging;
using ServiceMantle.Management;
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
            .Where(descriptor => descriptor.ServiceType == typeof(HostRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as HostRegistration)
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

        services.AddSingleton(new HostRegistration(
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
        services.TryAddSingleton<ExceptionMappingRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ProblemDetailsStartupValidator>());

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

    /// <summary>Adds the fixed ServiceMantle live and readiness endpoint capability.</summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">An optional bounded probe-timeout configuration.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The consuming service separately registers one <see cref="IServiceHealthSnapshotSource"/>.
    /// The live endpoint does not resolve that source; readiness fails closed when it is absent.
    /// The capability also registers the default scoped <see cref="IServiceReadinessDecisionSource"/>
    /// shared by the readiness endpoints and by optional packages that must not repeat the readiness
    /// algorithm. Registering a decision source before this call keeps that registration.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleHealthEndpoints(
        this ServiceMantleBuilder builder,
        Action<HealthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new HealthOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(new HealthRegistration(
            options.ProbeTimeout,
            options.ContributorTimeout));
        // Constructed explicitly so the shared contributor budget keeps measuring on
        // TimeProvider.System. The combiner also accepts a TimeProvider for deterministic tests,
        // and container-driven activation would otherwise start capturing a consumer-registered
        // TimeProvider and silently change this default.
        builder.Services.TryAddSingleton(serviceProvider =>
            new ServiceReadinessContributorCombiner(
                serviceProvider.GetServices<IServiceReadinessContributor>()));
        builder.Services.TryAddScoped<
            IServiceReadinessDecisionSource,
            ReadinessDecisionSource>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            HealthStartupValidator>());
        return builder;
    }

    /// <summary>Adds one ordered, read-only business readiness contributor.</summary>
    /// <typeparam name="TContributor">The contributor implementation type.</typeparam>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// Repeating the same implementation type is idempotent. Null contributors, order access
    /// failures, and duplicate orders fail when the host starts.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceReadinessContributor<TContributor>(
        this ServiceMantleBuilder builder)
        where TContributor : class, IServiceReadinessContributor
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IServiceReadinessContributor,
            TContributor>());
        return builder;
    }

    /// <summary>
    /// Adds the secure ServiceMantle management cookie authentication capability.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">An optional action that customizes the safe cookie lifetime settings.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The capability also registers the management authorization policy. Equivalent duplicate
    /// registrations are idempotent; conflicting or unsafe settings fail when the host starts.
    /// A presented cookie that cannot be authenticated uses the closed expired-session response so
    /// that invalid ticket details are not exposed.
    /// </remarks>
    public static ServiceMantleBuilder AddManagementCookieAuthentication(
        this ServiceMantleBuilder builder,
        Action<ManagementCookieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var hostRegistration = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(HostRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as HostRegistration)
            .Single(registration => registration is not null)!;
        var options = new ManagementCookieOptions();
        configure?.Invoke(options);
        var registration = ManagementCookieRegistration.Create(
            options,
            hostRegistration.ServiceId);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ManagementCookieRegistration));

        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.AddServiceMantleManagementAuthorization();
        builder.Services.AddDataProtection().SetApplicationName(registration.ApplicationName);
        builder.Services
            .AddAuthentication(authenticationOptions =>
            {
                authenticationOptions.DefaultAuthenticateScheme =
                    ManagementSessionDefaults.AuthenticationScheme;
                authenticationOptions.DefaultChallengeScheme =
                    ManagementSessionDefaults.AuthenticationScheme;
                authenticationOptions.DefaultForbidScheme =
                    ManagementSessionDefaults.AuthenticationScheme;
                authenticationOptions.DefaultSignInScheme =
                    ManagementSessionDefaults.AuthenticationScheme;
                authenticationOptions.DefaultSignOutScheme =
                    ManagementSessionDefaults.AuthenticationScheme;
            })
            .AddCookie(
                ManagementSessionDefaults.AuthenticationScheme,
                cookieOptions =>
                {
                    cookieOptions.Cookie.Name = ManagementSessionDefaults.CookieName;
                    cookieOptions.Cookie.HttpOnly = registration.HttpOnly;
                    cookieOptions.Cookie.SecurePolicy = registration.SecurePolicy;
                    cookieOptions.Cookie.SameSite = registration.SameSite;
                    cookieOptions.Cookie.IsEssential = registration.IsEssential;
                    cookieOptions.Cookie.Path = "/";
                    cookieOptions.Cookie.Domain = null;
                    cookieOptions.ExpireTimeSpan = registration.ExpireTimeSpan;
                    cookieOptions.SlidingExpiration = registration.SlidingExpiration;
                    cookieOptions.Events = ManagementCookieEvents.Create();
                });
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ManagementCookieStartupValidator>());

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

        builder.Services.AddSingleton<IExceptionMappingRegistration>(
            new ExceptionMappingRegistration<TException>(
                statusCode,
                errorCode,
                title,
                extensionFields));
        return builder;
    }

    /// <summary>Adds an explicit, startup-validated forwarded-header trust boundary.</summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">Configures trusted proxies, networks, hosts, and chain limit.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// This capability is opt-in and is not added by <c>AddServiceMantle</c>. Repeated normalized
    /// configurations are idempotent; conflicting registrations fail when the host starts.
    /// </remarks>
    public static ServiceMantleBuilder AddForwardedHeaders(
        this ServiceMantleBuilder builder,
        Action<ForwardedHeadersTrustOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ForwardedHeadersTrustOptions();
        configure(options);
        builder.Services.AddSingleton(new ForwardedHeadersRegistration(options));
        builder.Services.TryAddSingleton<ForwardedHeadersSnapshotProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ForwardedHeadersStartupValidator>());
        return builder;
    }

    /// <summary>Adds the mandatory security response-header capability.</summary>
    /// <remarks>This opt-in registration is idempotent and exposes no weakening options.</remarks>
    public static ServiceMantleBuilder AddSecurityResponseHeaders(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<SecurityResponseHeadersRegistration>();
        return builder;
    }

    /// <summary>Adds the isolated setup and management named rate-limit policies.</summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">Optionally configures the two sliding-window policies.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The policies count requests within the current process only, never queue rejected requests,
    /// and must be applied explicitly by name. This registration does not add a global limiter.
    /// Equivalent repeated registrations are idempotent; invalid or conflicting settings fail when
    /// the host starts. Place ASP.NET Core Rate Limiting after Authentication and before
    /// Authorization when applying the management policy.
    /// </remarks>
    public static ServiceMantleBuilder AddRateLimiting(
        this ServiceMantleBuilder builder,
        Action<RateLimitingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RateLimitingOptions();
        configure?.Invoke(options);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(RateLimitingRegistration));
        builder.Services.AddSingleton(new RateLimitingRegistration(options));
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<IManagementClaimsParser, ManagementClaimsParser>();
        builder.Services.TryAddSingleton<IManagementCurrentOperatorResolver, ManagementCurrentOperatorResolver>();
        builder.Services.TryAddSingleton<RateLimitingSnapshotProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            RateLimitingStartupValidator>());
        builder.Services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiterOptions.OnRejected = RateLimitingPolicy.OnRejectedAsync;
            rateLimiterOptions.AddPolicy<string>(
                RateLimitingDefaults.SetupPolicyName,
                RateLimitingPolicy.SetupPartition);
            rateLimiterOptions.AddPolicy<string>(
                RateLimitingDefaults.ManagementPolicyName,
                RateLimitingPolicy.ManagementPartition);
        });
        return builder;
    }

    /// <summary>Adds the immutable sensitive request Header registry and safe diagnostic projector.</summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">Adds product-independent or consumer-specific denied Header names.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// Built-in authentication, cookie, and API-key names cannot be removed. Repeated names merge
    /// case-insensitively. Invalid names, enumeration failures, and sanitizer ownership conflicts
    /// fail when the host starts without including Header or configuration values in diagnostics.
    /// </remarks>
    public static ServiceMantleBuilder AddSensitiveHeaders(
        this ServiceMantleBuilder builder,
        Action<SensitiveHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SensitiveHeadersOptions();
        var configureFailed = false;
        try
        {
            configure?.Invoke(options);
        }
        catch
        {
            configureFailed = true;
        }

        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(SensitiveHeaderRegistration));
        builder.Services.AddSingleton(new SensitiveHeaderRegistration(
            options,
            configureFailed));
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<SensitiveHeaderRegistry>(serviceProvider =>
            new SensitiveHeaderRegistry(
                serviceProvider.GetServices<SensitiveHeaderRegistration>()));
        builder.Services.TryAddSingleton<SensitiveHeaderSanitizer>(serviceProvider =>
            new SensitiveHeaderSanitizer(
                serviceProvider.GetRequiredService<SensitiveHeaderRegistry>()));
        builder.Services.TryAddSingleton<IStructuredLogSanitizerProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<SensitiveHeaderSanitizer>());
        builder.Services.TryAddSingleton<StructuredLogSanitizer>(serviceProvider =>
            serviceProvider.GetRequiredService<IStructuredLogSanitizerProvider>().Sanitizer);
        builder.Services.TryAddSingleton<RequestHeaderDiagnosticProjector>(serviceProvider =>
            new RequestHeaderDiagnosticProjector(
                serviceProvider.GetRequiredService<SensitiveHeaderSanitizer>()));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            SensitiveHeaderStartupValidator>());
        return builder;
    }
}

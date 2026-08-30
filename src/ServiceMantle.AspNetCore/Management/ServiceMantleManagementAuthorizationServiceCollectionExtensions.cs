using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Management;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the ServiceMantle management identity and authorization contract.
/// </summary>
public static class ServiceMantleManagementAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Claims parser, the current-operator resolver, the management permission
    /// requirement handler, and the <c>ServiceMantle.ManagementAdmin</c> policy.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// The entry point is standalone and idempotent. It registers no default authentication scheme
    /// and no concrete <see cref="IManagementIdentityProvider"/>, and it never calls a provider:
    /// cookie handling, login, and session flows belong to the consuming service, and an external
    /// authentication handler may equally produce a conforming principal.
    /// </remarks>
    public static IServiceCollection AddServiceMantleManagementAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IManagementClaimsParser, ManagementClaimsParser>();
        services.TryAddSingleton<IManagementCurrentOperatorResolver, ManagementCurrentOperatorResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuthorizationHandler,
            ManagementPermissionAuthorizationHandler>());

        services.AddAuthorization(options => options.AddPolicy(
            ManagementAuthorizationDefaults.AdminPolicyName,
            new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ManagementPermissionRequirement(ManagementPermission.Admin))
                .Build()));

        return services;
    }
}

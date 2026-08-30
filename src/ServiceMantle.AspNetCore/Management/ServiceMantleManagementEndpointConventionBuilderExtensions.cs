using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using ServiceMantle.Management;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Applies the ServiceMantle management authorization policy to endpoint conventions.
/// </summary>
public static class ServiceMantleManagementEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Requires the ServiceMantle management administrator policy on an endpoint or route group.
    /// </summary>
    /// <remarks>
    /// Applying this convention to a route group protects its child endpoints by default. An
    /// individual endpoint may opt out explicitly with <see cref="AuthorizationEndpointConventionBuilderExtensions.AllowAnonymous{TBuilder}(TBuilder)"/>.
    /// </remarks>
    public static TBuilder RequireServiceMantleManagementAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
    }
}

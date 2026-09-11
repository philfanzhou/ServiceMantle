using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the protected, versioned ServiceMantle management API v1 route group.
/// </summary>
public static class ServiceMantleManagementApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Creates the management API v1 route group with the fixed protection baseline applied.
    /// </summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns>The route group the consuming service maps its own children into.</returns>
    /// <remarks>
    /// Every child receives the <see cref="ManagementSurface.Management"/> surface, the
    /// <c>ServiceMantle.ManagementAdmin</c> policy, the <c>servicemantle.management</c> rate-limit
    /// policy, and the mandatory security response headers. The group itself maps no endpoint, so a
    /// host that never adds a child exposes nothing. A child may add a stricter authorization
    /// requirement; making a child anonymous, disabling or replacing its rate-limit policy, removing
    /// the security headers, or adding a second management surface fails before the host starts, as
    /// does mapping this group more than once per host. The reserved <c>/status</c>,
    /// <c>/bootstrap</c> and <c>/setup</c> branches keep their own surfaces and cannot be served
    /// from this group.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability is not registered, or its configured root is invalid.
    /// </exception>
    public static RouteGroupBuilder MapServiceMantleManagementApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var state = endpoints.ServiceProvider.GetService<ManagementApiState>() ??
            throw ManagementApiState.MissingCapability();
        state.RecordMap(endpoints);
        var group = endpoints.MapGroup(state.GetRootPath());
        group.WithServiceMantleManagementSurface(ManagementSurface.Management)
            .RequireServiceMantleManagementAdmin()
            .RequireRateLimiting(RateLimitingDefaults.ManagementPolicyName)
            .RequireServiceMantleSecurityResponseHeaders()
            // One marker per child endpoint: the startup baseline check must not reach an endpoint
            // the host mapped outside this group.
            .WithMetadata(new ManagementApiMetadata());
        return group;
    }
}

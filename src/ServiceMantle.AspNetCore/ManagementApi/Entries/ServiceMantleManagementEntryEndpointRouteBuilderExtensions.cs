using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Management;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the opt-in shared ServiceMantle management entries under the configured versioned root.
/// </summary>
public static class ServiceMantleManagementEntryEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps one shared management entry at its fixed path and methods with the fixed security
    /// baseline applied.
    /// </summary>
    /// <param name="endpoints">The application the ServiceMantle pipeline was composed on.</param>
    /// <param name="kind">The entry kind whose baseline is applied.</param>
    /// <param name="handler">The consuming service's handler for that entry.</param>
    /// <returns>The mapped endpoint, so the handler may add stricter conventions of its own.</returns>
    /// <remarks>
    /// The entry is mapped beside the protected group returned by
    /// <c>MapServiceMantleManagementApiV1</c>, never inside it, so it cannot be used to fake an
    /// anonymous exception on that group. ServiceMantle fixes the path, the methods, the phase-gate
    /// surface, the anonymous or policy authentication rule, the named rate-limit policy, the
    /// security response headers, and, for unsafe methods, the
    /// <c>X-ServiceMantle-Request: 1</c> guard. Mapping the same kind twice, a wrong path or method,
    /// a downgraded convention, a missing capability, or an entry mapped through the protected group
    /// fails before the host starts. This method maps no business handler of its own.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined.</exception>
    /// <exception cref="InvalidOperationException">
    /// The management entry capability is not registered, or the versioned root is invalid.
    /// </exception>
    public static RouteHandlerBuilder MapServiceMantleManagementEntry(
        this IEndpointRouteBuilder endpoints,
        ServiceMantleManagementEntryKind kind,
        Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var state = endpoints.ServiceProvider.GetService<ServiceMantleManagementEntryState>() ??
            throw ServiceMantleManagementEntryState.MissingCapability();
        var root = state.GetRootPath();
        var definition = ServiceMantleManagementEntryDefaults.Get(kind);
        state.RecordMap(endpoints);

        var builder = endpoints.MapMethods(root + definition.PathSuffix, definition.Methods, handler);
        builder
            .WithServiceMantleManagementSurface(definition.Surface)
            .RequireRateLimiting(definition.RateLimitPolicyName)
            .RequireServiceMantleSecurityResponseHeaders()
            .WithMetadata(new ServiceMantleManagementEntryMetadata(kind));
        if (definition.IsAnonymous)
        {
            builder.AllowAnonymous();
        }
        else
        {
            builder.RequireAuthorization(definition.AuthorizationPolicyName!);
        }

        if (definition.RequiresUnsafeRequestHeader)
        {
            builder.AddEndpointFilter(ServiceMantleUnsafeRequestFilter.Instance);
            builder.WithMetadata(new ServiceMantleUnsafeRequestGuardMetadata());
        }

        return builder;
    }
}

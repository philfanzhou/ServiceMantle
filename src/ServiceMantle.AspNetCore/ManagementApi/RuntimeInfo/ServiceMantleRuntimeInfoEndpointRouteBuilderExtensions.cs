using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the protected runtime information endpoint of the ServiceMantle management API v1 surface.
/// </summary>
public static class ServiceMantleRuntimeInfoEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /runtime</c> into the group returned by <c>MapServiceMantleManagementApiV1</c>.
    /// </summary>
    /// <param name="endpoints">The protected management API v1 route group.</param>
    /// <returns>A convention builder for the single mapped endpoint.</returns>
    /// <remarks>
    /// The endpoint is opt-in: the management API v1 baseline never adds it, so a host that does not
    /// call this method exposes nothing here. With the default root the full path is
    /// <c>/management/v1/runtime</c>; a custom versioned root moves it with the group. The response
    /// is a fixed <c>200 application/json</c> body containing exactly <c>serviceName</c>,
    /// <c>serviceVersion</c>, <c>instanceId</c> and <c>phase</c>. The first three come from the
    /// immutable <see cref="ServiceLogContext"/> registered by <c>AddServiceMantle</c>; the last is
    /// the constant <c>Completed</c>, which states that this request passed the phase gate's
    /// observation. The handler reads no snapshot source of its own, declares no query or body
    /// parameter, and keeps the group's surface, authorization, rate limit, security headers and
    /// Correlation ID untouched.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability or the host identity is not registered, or the endpoint is
    /// mapped more than once. A mapping onto a route group other than the management API v1 group
    /// fails the same way before the host starts.
    /// </exception>
    public static IEndpointConventionBuilder MapServiceMantleRuntimeInfo(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ServiceMantleManagementApiState>() ??
            throw ServiceMantleRuntimeInfoMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            // The baseline owns the root's own diagnostics; this entry point never repeats a
            // configured value of its own.
            throw ServiceMantleRuntimeInfoMapping.Failure();
        }

        var logContext = services.GetService<ServiceLogContext>() ?? throw ServiceMantleRuntimeInfoMapping.Failure();
        ServiceMantleRuntimeInfoMapping.RecordMap(services);
        // The four projected values are fixed for the life of the process, so the exact body is
        // built once and no request-scoped object ever reaches a serializer.
        var result = ServiceMantleRuntimeInfoResult.Create(logContext);
        var expectedPath = root + ServiceMantleRuntimeInfoMapping.RoutePath;
        var endpoint = endpoints.MapGet(ServiceMantleRuntimeInfoMapping.RoutePath, () => result);
        endpoint.Finally(builder => ServiceMantleRuntimeInfoMapping.Validate(builder, expectedPath));
        return endpoint;
    }
}

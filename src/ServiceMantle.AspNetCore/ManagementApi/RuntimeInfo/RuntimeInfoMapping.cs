using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo;

/// <summary>
/// Owns the endpoint-local mapping rules of the management API v1 runtime information endpoint.
/// </summary>
internal static class RuntimeInfoMapping
{
    /// <summary>The single route the endpoint adds below the versioned management root.</summary>
    internal const string RoutePath = "/runtime";

    /// <summary>
    /// Counts the mappings per host. The application's service provider is already built when an
    /// endpoint is mapped, so this per-host state cannot live in the container, and the endpoint is
    /// explicitly not allowed to extend the shared management API v1 registration.
    /// </summary>
    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    /// <summary>
    /// Rejects an endpoint that did not end up as a direct child of the protected management API v1
    /// group. The check runs as a final convention, so it sees the group's own metadata and the
    /// combined route pattern, and it fails while the endpoints are built rather than on a request.
    /// </summary>
    internal static void Validate(EndpointBuilder builder, string expectedPath)
    {
        if (builder is not RouteEndpointBuilder route ||
            !string.Equals(route.RoutePattern.RawText, expectedPath, StringComparison.Ordinal) ||
            builder.Metadata.OfType<ManagementApiMetadata>().Count() != 1)
        {
            throw Failure();
        }
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management API v1 runtime information endpoint must be mapped at most " +
            "once, and only on the route group returned by MapServiceMantleManagementApiV1.");
}

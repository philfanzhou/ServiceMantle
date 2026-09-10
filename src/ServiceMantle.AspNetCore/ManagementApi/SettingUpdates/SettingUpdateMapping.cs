using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

internal static class SettingUpdateMapping
{
    internal const string Path = "/settings";
    internal const string UnavailableErrorCode = "management.settings.update_unavailable";

    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    internal static void Validate(EndpointBuilder builder, string expectedPath)
    {
        if (builder is not RouteEndpointBuilder route
            || !string.Equals(route.RoutePattern.RawText, expectedPath, StringComparison.Ordinal)
            || builder.Metadata.OfType<ManagementApiMetadata>().Count() != 1)
        {
            throw Failure();
        }
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management API v1 setting update endpoint must be mapped at most " +
            "once, and only on the route group returned by MapServiceMantleManagementApiV1.");

    internal static InvalidOperationException MissingExecutor() =>
        new("The ServiceMantle management API v1 setting update endpoint requires an explicit " +
            "consumer-owned transaction executor.");
}

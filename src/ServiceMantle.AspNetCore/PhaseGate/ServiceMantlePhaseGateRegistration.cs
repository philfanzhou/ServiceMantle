using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore;

internal sealed record ServiceMantlePhaseGateRegistration(string Prefix, TimeSpan Timeout);

internal sealed class ServiceMantlePhaseGateState(IEnumerable<ServiceMantlePhaseGateRegistration> registrations)
{
    private IEndpointRouteBuilder? endpoints;
    private int useCount;
    internal ServiceMantlePhaseGateRegistration GetConfiguration()
    {
        ServiceMantlePhaseGateRegistration? baseline = null;
        foreach (var item in registrations)
        {
            var normalized = new ServiceMantlePhaseGateRegistration(Normalize(item.Prefix), item.Timeout);
            if (normalized.Timeout < TimeSpan.FromMilliseconds(50) || normalized.Timeout > TimeSpan.FromSeconds(30) ||
                baseline is not null && baseline != normalized) throw Failure();
            baseline = normalized;
        }
        return baseline ?? throw Failure();
    }

    internal void RecordUse(IEndpointRouteBuilder routeBuilder)
    {
        endpoints = routeBuilder;
        useCount++;
    }

    internal void Validate()
    {
        var configuration = GetConfiguration();
        if (useCount != 1 || endpoints is null) throw Failure();
        var managedRoutes = new List<RouteEndpoint>();
        foreach (var endpoint in endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var path = endpoint.RoutePattern.RawText ?? "";
            var markers = endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementSurfaceMetadata>();
            if (markers.Count > 1 || markers.Any(marker => !Enum.IsDefined(marker.Surface))) throw Failure();
            if (markers.Count == 1)
            {
                if (!Matches(path, configuration.Prefix, markers[0].Surface)) throw Failure();
                if (managedRoutes.Any(previous => string.Equals(previous.RoutePattern.RawText?.TrimEnd('/'), path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                    MethodsOverlap(previous, endpoint))) throw Failure();
                managedRoutes.Add(endpoint);
                // A dynamic first management child could overlap /bootstrap, /setup, or /status.
                var prefixSegments = configuration.Prefix.Count(character => character == '/');
                if (markers[0].Surface == ServiceMantleManagementSurface.Management &&
                    endpoint.RoutePattern.PathSegments.Count > prefixSegments &&
                    endpoint.RoutePattern.PathSegments[prefixSegments].Parts.Any(part => part is not RoutePatternLiteralPart))
                    throw Failure();
                if (markers[0].Surface == ServiceMantleManagementSurface.Status && !ReadOnly(endpoint)) throw Failure();
            }
            else if (Under(path, configuration.Prefix)) throw Failure();
        }
    }

    internal static bool Matches(string path, string prefix, ServiceMantleManagementSurface surface) => surface switch
    {
        ServiceMantleManagementSurface.Status => Under(path, prefix + "/status"),
        ServiceMantleManagementSurface.Bootstrap => Under(path, prefix + "/bootstrap"),
        ServiceMantleManagementSurface.Setup => Under(path, prefix + "/setup"),
        ServiceMantleManagementSurface.Management => Under(path, prefix) &&
            !Under(path, prefix + "/status") && !Under(path, prefix + "/bootstrap") && !Under(path, prefix + "/setup"),
        _ => false
    };

    internal static bool Under(string path, string prefix) =>
        new PathString(path).StartsWithSegments(new PathString(prefix), StringComparison.OrdinalIgnoreCase);

    private static bool MethodsOverlap(RouteEndpoint first, RouteEndpoint second)
    {
        var firstMethods = first.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var secondMethods = second.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return firstMethods is null or { Count: 0 } || secondMethods is null or { Count: 0 } ||
            firstMethods.Intersect(secondMethods, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool ReadOnly(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods is { Count: > 0 } && methods.All(method => HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
    }

    private static string Normalize(string? path)
    {
        if (path is null) throw Failure();
        var normalized = path.Trim().ToLowerInvariant();
        if (normalized.EndsWith('/')) normalized = normalized[..^1];
        if (normalized.Length is < 2 or > 128 || normalized[0] != '/' || normalized.EndsWith('/')) throw Failure();
        foreach (var segment in normalized[1..].Split('/'))
        {
            if (segment.Length == 0 || segment.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))) throw Failure();
        }
        if (Under(normalized, "/health") || Under("/health", normalized)) throw Failure();
        return normalized;
    }

    internal static InvalidOperationException Failure() => new("The ServiceMantle phase gate configuration or endpoint mapping is invalid.");
}

internal sealed class ServiceMantlePhaseGateStartupValidator(ServiceMantlePhaseGateState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Validate();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

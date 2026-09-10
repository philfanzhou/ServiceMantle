using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.OpenTelemetry.Prometheus;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the authorized ServiceMantle Prometheus scraping endpoint.</summary>
public static class ServiceMantlePrometheusEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the configured GET/HEAD scrape endpoint, or maps nothing when the capability is disabled.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The route builder, for composition.</returns>
    public static IEndpointRouteBuilder MapServiceMantlePrometheusEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var snapshotProvider = endpoints.ServiceProvider
            .GetService<PrometheusSnapshotProvider>() ??
            throw new InvalidOperationException(
                "The ServiceMantle Prometheus endpoint requires AddOpenTelemetryPrometheusEndpoint.");

        if (!snapshotProvider.TryGetMappingSnapshot(out var snapshot))
        {
            return endpoints;
        }

        var state = endpoints.ServiceProvider.GetRequiredService<PrometheusEndpointState>();
        state.RecordMapping(endpoints);
        var pipeline = endpoints.CreateApplicationBuilder();
        pipeline.UseMiddleware<PrometheusScrapeGate>();
        pipeline.UseOpenTelemetryPrometheusScrapingEndpoint(
            meterProvider: null,
            predicate: static _ => true,
            path: null,
            configureBranchedPipeline: null,
            optionsName: null);
        endpoints.MapMethods(
                snapshot!.EndpointPath,
                [HttpMethods.Get, HttpMethods.Head],
                pipeline.Build())
            .WithMetadata(new PrometheusEndpointMetadata())
            .RequireAuthorization(snapshot.AuthorizationPolicyName!);

        return endpoints;
    }
}

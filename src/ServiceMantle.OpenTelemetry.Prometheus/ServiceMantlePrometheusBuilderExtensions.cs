using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using ServiceMantle.AspNetCore;
using ServiceMantle.OpenTelemetry.Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the ServiceMantle-owned Prometheus scraping capability.</summary>
public static class ServiceMantlePrometheusBuilderExtensions
{
    /// <summary>Adds a default-disabled, authorized Prometheus exporter and endpoint registration.</summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">An optional action that explicitly enables and configures the endpoint.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// Call <c>MapServiceMantlePrometheusEndpoint</c> after building the application. Equivalent
    /// registrations are idempotent; invalid or conflicting settings fail when the host starts.
    /// </remarks>
    public static ServiceMantleBuilder AddOpenTelemetryPrometheusEndpoint(
        this ServiceMantleBuilder builder,
        Action<ServiceMantlePrometheusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceMantlePrometheusOptions();
        configure?.Invoke(options);
        var registration = new ServiceMantlePrometheusRegistration(options);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ServiceMantlePrometheusRegistration));

        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<ServiceMantlePrometheusSnapshotProvider>();
        builder.Services.TryAddSingleton<ServiceMantlePrometheusEndpointState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantlePrometheusStartupValidator>());

        if (!options.Enabled)
        {
            return builder;
        }

        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddPrometheusExporter(
            exporterOptions =>
            {
                exporterOptions.ScrapeEndpointPath = ServiceMantlePrometheusDefaults.EndpointPath;
                exporterOptions.MaxScrapeResponseSizeBytes =
                    ServiceMantlePrometheusDefaults.MaximumResponseSizeBytes;
                exporterOptions.ScopeInfoEnabled = false;
                exporterOptions.TargetInfoEnabled = false;
                exporterOptions.ResourceConstantLabels = null;
            }));

        return builder;
    }
}

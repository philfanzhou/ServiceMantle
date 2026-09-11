using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
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
        Action<PrometheusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new PrometheusOptions();
        configure?.Invoke(options);
        var registration = new PrometheusRegistration(options);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(PrometheusRegistration));

        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<PrometheusSnapshotProvider>();
        builder.Services.TryAddSingleton<PrometheusEndpointState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            PrometheusStartupValidator>());

        if (!options.Enabled)
        {
            return builder;
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<PrometheusAspNetCoreOptions>,
            PrometheusExporterOptionsPolicy>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<PrometheusAspNetCoreOptions>,
            PrometheusExporterOptionsPolicy>());
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddPrometheusExporter());

        return builder;
    }
}

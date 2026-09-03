using OpenTelemetry.Metrics;
using ServiceMantle.AspNetCore;
using ServiceMantle.Logging;
using ServiceMantle.OpenTelemetry;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers finite service identity and installation phase metrics.</summary>
public static class ServiceMantleMetricsBuilderExtensions
{
    /// <summary>Adds fixed metrics, independently of HTTP/runtime instrumentation and exporters.</summary>
    /// <remarks>
    /// Repeated calls are idempotent. Each host owns one publisher and exports only that publisher's
    /// instruments. No custom tags are accepted. Resolve ServiceMantleMetrics to publish observed
    /// phases; the caller owns the correctness and freshness of those observations.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleMetrics(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ServiceMantleMetrics)))
        {
            return builder;
        }
        builder.Services.AddSingleton(_ => new ServiceMantleMetrics());
        builder.Services.AddOpenTelemetry().WithMetrics();
        builder.Services.ConfigureOpenTelemetryMeterProvider((services, provider) =>
        {
            var publisher = services.GetRequiredService<ServiceMantleMetrics>();
            var identity = services.GetRequiredService<ServiceLogContext>();
            provider.SetResourceBuilder(ServiceMantleOpenTelemetryBuilderExtensions.CreateResource(identity))
                .AddMeter(ServiceMantleMetrics.MeterName)
                .AddView(instrument =>
                {
                    if (instrument.Meter.Name != ServiceMantleMetrics.MeterName) return null;
                    if (!ReferenceEquals(instrument.Meter, publisher.Meter)) return MetricStreamConfiguration.Drop;
                    return instrument.Name switch
                    {
                        ServiceMantleMetrics.ServiceInfoName => new MetricStreamConfiguration
                        {
                            TagKeys = [],
                            CardinalityLimit = 1
                        },
                        ServiceMantleMetrics.InstallationPhaseName => new MetricStreamConfiguration
                        {
                            TagKeys = ["phase"],
                            CardinalityLimit = 4
                        },
                        _ => MetricStreamConfiguration.Drop
                    };
                })
                .AddInstrumentation(() => publisher);
        });
        return builder;
    }
}

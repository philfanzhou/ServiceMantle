using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore;
using ServiceMantle.OpenTelemetry.Otlp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the isolated optional ServiceMantle OTLP exporters.</summary>
public static class ServiceMantleOtlpBuilderExtensions
{
    /// <summary>
    /// Adds independently enabled OTLP trace and metric exporters. Both signals are disabled by
    /// default, in which case no exporter, provider, authentication resolution, or background
    /// activity is registered.
    /// </summary>
    public static ServiceMantleBuilder AddOpenTelemetryOtlpExporter(
        this ServiceMantleBuilder builder,
        Action<OtlpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new OtlpOptions();
        configure?.Invoke(options);
        var registration = OtlpRegistration.Create(options);
        builder.Services.AddSingleton(registration);

        if (!registration.Traces.Enabled && !registration.Metrics.Enabled)
        {
            return builder;
        }

        var firstEnabledRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(OtlpRuntime));
        if (!firstEnabledRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<OtlpRuntime>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<OtlpExporterOptions>,
            OtlpOptionsConfigurator>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            OtlpStartupValidator>());

        var openTelemetry = builder.Services.AddOpenTelemetry();
        if (registration.Traces.Enabled)
        {
            openTelemetry.WithTracing(tracing => tracing.AddOtlpExporter(
                OtlpNames.Traces,
                configure: null));
        }

        if (registration.Metrics.Enabled)
        {
            openTelemetry.WithMetrics(metrics => metrics.AddOtlpExporter(
                OtlpNames.Metrics,
                (_, reader) =>
                {
                    reader.PeriodicExportingMetricReaderOptions = new PeriodicExportingMetricReaderOptions
                    {
                        ExportIntervalMilliseconds = SafeMilliseconds(
                            registration.Metrics.BatchDelay,
                            fallback: 5_000),
                        ExportTimeoutMilliseconds = SafeMilliseconds(
                            registration.Metrics.ExportTimeout,
                            fallback: 10_000),
                    };
                }));
        }

        return builder;
    }

    private static int SafeMilliseconds(TimeSpan value, int fallback)
    {
        var milliseconds = value.TotalMilliseconds;
        return milliseconds is >= 1 and <= int.MaxValue && milliseconds == Math.Truncate(milliseconds)
            ? (int)milliseconds
            : fallback;
    }
}

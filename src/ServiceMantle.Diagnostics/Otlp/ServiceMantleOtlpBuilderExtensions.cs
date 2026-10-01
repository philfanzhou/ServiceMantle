using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.Configuration;
using ServiceMantle.Web;
using ServiceMantle.Diagnostics.Export.Otlp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the isolated optional ServiceMantle OTLP exporters.</summary>
public static class ServiceMantleOtlpBuilderExtensions
{
    /// <summary>
    /// Registers the OTLP exporters driven by the activated setting snapshot's
    /// <c>opentelemetry.otlp_endpoint</c> value instead of explicit endpoints.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="snapshot">The activated setting snapshot.</param>
    /// <param name="configure">
    /// An optional action over the exporter options. It selects which signals are enabled and any
    /// remaining options; the classification owns every enabled signal's endpoint. A configure
    /// action that enables no signal leaves the exporters off.
    /// </param>
    /// <returns>
    /// The classified state. A usable endpoint registers the exporters through the existing
    /// <see cref="AddOpenTelemetryOtlpExporter"/> entry and its validation rules; an empty value
    /// registers no exporter, provider, or background activity at all; an unusable value does the
    /// same and the consumer records the fixed <see cref="OtlpSettingState.Category"/> warning
    /// after startup.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The registration also contributes <see cref="OtlpSettingDefinitions"/> to the global
    /// setting catalog (the definition and the strict management-update validation). No
    /// <c>IConfiguration</c> is read and no value is written back.
    /// </para>
    /// <para>
    /// Repeating this call is idempotent under the underlying entry's own idempotency rules; a
    /// snapshot whose endpoint is empty or unusable never registers exporter machinery, even when
    /// the optional configuration enabled signals.
    /// </para>
    /// </remarks>
    public static OtlpSettingState AddOpenTelemetryOtlpExporterFromSettings(
        this ServiceMantleBuilder builder,
        ServiceSettingSnapshot snapshot,
        Action<OtlpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var state = OtlpSettingState.Classify(snapshot);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IServiceSettingDefinitionProvider,
            OtlpSettingDefinitions>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IServiceSettingCompositeValidator,
            OtlpSettingDefinitions>());

        if (state.Endpoint is null)
        {
            // Empty or unusable: the capability is off with zero exporter machinery; the state's
            // category tells the consumer what to warn about.
            return state;
        }

        builder.AddOpenTelemetryOtlpExporter(options =>
        {
            configure?.Invoke(options);
            if (options.Traces.Enabled && options.Traces.Endpoint is null)
            {
                options.Traces.Endpoint = state.Endpoint;
            }

            if (options.Metrics.Enabled && options.Metrics.Endpoint is null)
            {
                options.Metrics.Endpoint = state.Endpoint;
            }
        });

        return state;
    }

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

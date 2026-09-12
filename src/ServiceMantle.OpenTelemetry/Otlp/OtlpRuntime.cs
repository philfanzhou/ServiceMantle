using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using ServiceMantle.Diagnostics;

namespace ServiceMantle.OpenTelemetry.Otlp;

internal enum OtlpSignal
{
    Traces,
    Metrics,
}

internal sealed record OtlpSignalRegistration(
    bool Enabled,
    OtlpProtocol Protocol,
    Uri? Endpoint,
    bool EndpointRejected,
    string? AuthenticationHeaderName,
    bool AllowInsecureLoopbackForTesting,
    TimeSpan ExportTimeout,
    TimeSpan BatchDelay,
    int MaxQueueSize,
    int MaxExportBatchSize);

internal sealed record OtlpRegistration(
    OtlpSignalRegistration Traces,
    OtlpSignalRegistration Metrics)
{
    internal static OtlpRegistration Create(OtlpOptions options) => new(
        FromTrace(options.Traces),
        FromMetric(options.Metrics));

    private static OtlpSignalRegistration FromTrace(
        OtlpTraceOptions options) => new(
        options.Enabled,
        options.Protocol,
        options.Endpoint,
        options.EndpointContainedUnsafeComponents,
        options.AuthenticationHeaderName,
        options.AllowInsecureLoopbackForTesting,
        options.ExportTimeout,
        options.BatchDelay,
        options.MaxQueueSize,
        options.MaxExportBatchSize);

    private static OtlpSignalRegistration FromMetric(
        OtlpMetricOptions options) => new(
        options.Enabled,
        options.Protocol,
        options.Endpoint,
        options.EndpointContainedUnsafeComponents,
        options.AuthenticationHeaderName,
        options.AllowInsecureLoopbackForTesting,
        options.ExportTimeout,
        options.BatchDelay,
        MaxQueueSize: 0,
        MaxExportBatchSize: 0);
}

internal sealed record OtlpSignalConfiguration(
    bool Enabled,
    OtlpExportProtocol Protocol,
    Uri? Endpoint,
    string? Headers,
    int ExportTimeoutMilliseconds,
    int BatchDelayMilliseconds,
    int MaxQueueSize,
    int MaxExportBatchSize)
{
    internal OtlpSignalConfiguration WithHeaders(string? headers) => this with
    {
        Headers = headers,
    };

    public override string ToString() =>
        $"OtlpSignalConfiguration(Enabled={Enabled}, Protocol={Protocol}, " +
        $"HasEndpoint={Endpoint is not null}, HasAuthentication={Headers is not null})";
}

internal sealed class OtlpRuntime
{
    private const int MinimumExportTimeoutMilliseconds = 1_000;
    private const int MaximumExportTimeoutMilliseconds = 30_000;
    private const int MinimumBatchDelayMilliseconds = 100;
    private const int MaximumBatchDelayMilliseconds = 30_000;
    private const int MinimumQueueSize = 100;
    private const int MaximumQueueSize = 50_000;
    private const int MinimumBatchSize = 1;
    private const int MaximumBatchSize = 1_000;

    private readonly object sync = new();
    private readonly IReadOnlyList<OtlpRegistration> registrations;
    private readonly IRemoteTelemetryAuthenticationResolver? headerResolver;
    private OtlpSignalConfiguration? traces;
    private OtlpSignalConfiguration? metrics;

    public OtlpRuntime(
        IEnumerable<OtlpRegistration> registrations,
        IRemoteTelemetryAuthenticationResolver? headerResolver = null)
    {
        this.registrations = registrations.ToList().AsReadOnly();
        this.headerResolver = headerResolver;
    }

    internal void Validate()
    {
        _ = Get(OtlpSignal.Traces);
        _ = Get(OtlpSignal.Metrics);
    }

    internal OtlpSignalConfiguration Get(OtlpSignal signal)
    {
        lock (sync)
        {
            var cached = signal == OtlpSignal.Traces ? traces : metrics;
            if (cached is not null)
            {
                return cached;
            }

            OtlpSignalConfiguration? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(
                    signal,
                    signal == OtlpSignal.Traces
                        ? registration.Traces
                        : registration.Metrics);
                if (baseline is not null && baseline != candidate)
                {
                    throw Failure(
                        "registration",
                        WellKnownOtlpErrorCodes.ConflictingRegistration);
                }

                baseline = candidate;
            }

            baseline ??= Disabled();
            var resolved = baseline.Enabled ? ResolveHeader(signal, baseline) : baseline;
            if (signal == OtlpSignal.Traces)
            {
                traces = resolved;
            }
            else
            {
                metrics = resolved;
            }

            return resolved;
        }
    }

    private static OtlpSignalConfiguration Normalize(
        OtlpSignal signal,
        OtlpSignalRegistration registration)
    {
        if (!registration.Enabled)
        {
            return Disabled();
        }

        var fieldPrefix = signal == OtlpSignal.Traces ? "traces" : "metrics";
        if (!Enum.IsDefined(registration.Protocol))
        {
            throw Failure(
                $"{fieldPrefix}.protocol",
                WellKnownOtlpErrorCodes.InvalidProtocol);
        }

        if (registration.EndpointRejected)
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownOtlpErrorCodes.InvalidEndpoint);
        }

        var endpoint = registration.Endpoint;
        if (endpoint is null)
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownOtlpErrorCodes.EndpointRequired);
        }

        ValidateEndpoint(registration, endpoint, fieldPrefix);
        var exportTimeout = Milliseconds(
            registration.ExportTimeout,
            MinimumExportTimeoutMilliseconds,
            MaximumExportTimeoutMilliseconds,
            $"{fieldPrefix}.exportTimeout",
            WellKnownOtlpErrorCodes.InvalidExportTimeout);
        var batchDelay = Milliseconds(
            registration.BatchDelay,
            MinimumBatchDelayMilliseconds,
            MaximumBatchDelayMilliseconds,
            $"{fieldPrefix}.batchDelay",
            WellKnownOtlpErrorCodes.InvalidBatchDelay);
        var maxQueueSize = 0;
        var maxBatchSize = 0;
        if (signal == OtlpSignal.Traces)
        {
            maxQueueSize = registration.MaxQueueSize;
            if (maxQueueSize is < MinimumQueueSize or > MaximumQueueSize)
            {
                throw Failure(
                    "traces.maxQueueSize",
                    WellKnownOtlpErrorCodes.InvalidQueueSize);
            }

            maxBatchSize = registration.MaxExportBatchSize;
            if (maxBatchSize is < MinimumBatchSize or > MaximumBatchSize ||
                maxBatchSize > maxQueueSize)
            {
                throw Failure(
                    "traces.maxExportBatchSize",
                    WellKnownOtlpErrorCodes.InvalidBatchSize);
            }
        }

        var headerName = registration.AuthenticationHeaderName?.Trim();
        if (headerName is not null && !IsSafeResolverName(headerName))
        {
            throw Failure(
                $"{fieldPrefix}.authenticationHeaderName",
                WellKnownOtlpErrorCodes.AuthenticationInvalid);
        }

        return new OtlpSignalConfiguration(
            Enabled: true,
            registration.Protocol == OtlpProtocol.Grpc
                ? OtlpExportProtocol.Grpc
                : OtlpExportProtocol.HttpProtobuf,
            endpoint,
            headerName,
            exportTimeout,
            batchDelay,
            maxQueueSize,
            maxBatchSize);
    }

    private OtlpSignalConfiguration ResolveHeader(
        OtlpSignal signal,
        OtlpSignalConfiguration configuration)
    {
        var resolverName = configuration.Headers;
        if (resolverName is null)
        {
            return configuration;
        }

        RemoteTelemetryAuthenticationHeader? header;
        try
        {
            if (headerResolver is null || !headerResolver.TryResolve(resolverName, out header) || header is null)
            {
                throw Failure(
                    AuthenticationField(signal),
                    WellKnownOtlpErrorCodes.AuthenticationMissing);
            }
        }
        catch (OtlpConfigurationException)
        {
            throw;
        }
        catch
        {
            throw Failure(
                AuthenticationField(signal),
                WellKnownOtlpErrorCodes.AuthenticationMissing);
        }

        if (!IsHttpToken(header.Name) ||
            string.IsNullOrEmpty(header.Value) ||
            header.Value.Any(character => character is '\r' or '\n'))
        {
            throw Failure(
                AuthenticationField(signal),
                WellKnownOtlpErrorCodes.AuthenticationInvalid);
        }

        return configuration.WithHeaders(
            $"{header.Name}={Uri.EscapeDataString(header.Value)}");
    }

    private static void ValidateEndpoint(
        OtlpSignalRegistration registration,
        Uri endpoint,
        string fieldPrefix)
    {
        try
        {
            if (!endpoint.IsAbsoluteUri ||
                string.IsNullOrEmpty(endpoint.Host) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw Failure(
                    $"{fieldPrefix}.endpoint",
                    WellKnownOtlpErrorCodes.InvalidEndpoint);
            }

            if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    !registration.AllowInsecureLoopbackForTesting ||
                    !endpoint.IsLoopback)
                {
                    throw Failure(
                        $"{fieldPrefix}.endpoint",
                        WellKnownOtlpErrorCodes.InsecureEndpoint);
                }
            }

        }
        catch (OtlpConfigurationException)
        {
            throw;
        }
        catch
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownOtlpErrorCodes.InvalidEndpoint);
        }
    }

    private static int Milliseconds(
        TimeSpan value,
        int minimum,
        int maximum,
        string fieldName,
        string errorCode)
    {
        var milliseconds = value.TotalMilliseconds;
        if (milliseconds < minimum || milliseconds > maximum || milliseconds != Math.Truncate(milliseconds))
        {
            throw Failure(fieldName, errorCode);
        }

        return (int)milliseconds;
    }

    private static bool IsSafeResolverName(string value)
    {
        if (value.Length is < 1 or > 128)
        {
            return false;
        }

        return value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
    }

    private static bool IsHttpToken(string value)
    {
        if (value.Length is < 1 or > 128)
        {
            return false;
        }

        const string separators = "()<>@,;:\\\"/[]?={} \t";
        return value.All(character => character is > '\u001f' and < '\u007f' && !separators.Contains(character));
    }

    private static string AuthenticationField(OtlpSignal signal) =>
        signal == OtlpSignal.Traces
            ? "traces.authenticationHeaderName"
            : "metrics.authenticationHeaderName";

    private static OtlpSignalConfiguration Disabled() => new(
        Enabled: false,
        OtlpExportProtocol.Grpc,
        Endpoint: null,
        Headers: null,
        ExportTimeoutMilliseconds: 0,
        BatchDelayMilliseconds: 0,
        MaxQueueSize: 0,
        MaxExportBatchSize: 0);

    private static OtlpConfigurationException Failure(
        string fieldName,
        string errorCode) => new(fieldName, errorCode);
}

internal sealed class OtlpOptionsConfigurator(OtlpRuntime runtime)
    : IConfigureNamedOptions<OtlpExporterOptions>
{
    public void Configure(OtlpExporterOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OtlpExporterOptions options)
    {
        OtlpSignal? signal = name switch
        {
            OtlpNames.Traces => OtlpSignal.Traces,
            OtlpNames.Metrics => OtlpSignal.Metrics,
            _ => null,
        };
        if (signal is null)
        {
            return;
        }

        var configuration = runtime.Get(signal.Value);
        if (!configuration.Enabled)
        {
            return;
        }

        options.Protocol = configuration.Protocol;
        options.Endpoint = configuration.Endpoint!;
        options.Headers = configuration.Headers;
        options.TimeoutMilliseconds = configuration.ExportTimeoutMilliseconds;
        if (signal == OtlpSignal.Traces)
        {
            options.ExportProcessorType = ExportProcessorType.Batch;
            options.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions
            {
                ExporterTimeoutMilliseconds = configuration.ExportTimeoutMilliseconds,
                ScheduledDelayMilliseconds = configuration.BatchDelayMilliseconds,
                MaxQueueSize = configuration.MaxQueueSize,
                MaxExportBatchSize = configuration.MaxExportBatchSize,
            };
        }
    }
}

internal sealed class OtlpStartupValidator(OtlpRuntime runtime)
    : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class OtlpNames
{
    internal const string Traces = "ServiceMantle.Otlp.Traces";
    internal const string Metrics = "ServiceMantle.Otlp.Metrics";
}

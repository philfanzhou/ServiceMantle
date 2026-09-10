using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace ServiceMantle.OpenTelemetry.Otlp;

internal enum ServiceMantleOtlpSignal
{
    Traces,
    Metrics,
}

internal sealed record ServiceMantleOtlpSignalRegistration(
    bool Enabled,
    ServiceMantleOtlpProtocol Protocol,
    Uri? Endpoint,
    bool EndpointRejected,
    string? AuthenticationHeaderName,
    bool AllowInsecureLoopbackForTesting,
    TimeSpan ExportTimeout,
    TimeSpan BatchDelay,
    int MaxQueueSize,
    int MaxExportBatchSize);

internal sealed record ServiceMantleOtlpRegistration(
    ServiceMantleOtlpSignalRegistration Traces,
    ServiceMantleOtlpSignalRegistration Metrics)
{
    internal static ServiceMantleOtlpRegistration Create(ServiceMantleOtlpOptions options) => new(
        FromTrace(options.Traces),
        FromMetric(options.Metrics));

    private static ServiceMantleOtlpSignalRegistration FromTrace(
        ServiceMantleOtlpTraceOptions options) => new(
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

    private static ServiceMantleOtlpSignalRegistration FromMetric(
        ServiceMantleOtlpMetricOptions options) => new(
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

internal sealed record ServiceMantleOtlpSignalConfiguration(
    bool Enabled,
    OtlpExportProtocol Protocol,
    Uri? Endpoint,
    string? Headers,
    int ExportTimeoutMilliseconds,
    int BatchDelayMilliseconds,
    int MaxQueueSize,
    int MaxExportBatchSize)
{
    internal ServiceMantleOtlpSignalConfiguration WithHeaders(string? headers) => this with
    {
        Headers = headers,
    };

    public override string ToString() =>
        $"ServiceMantleOtlpSignalConfiguration(Enabled={Enabled}, Protocol={Protocol}, " +
        $"HasEndpoint={Endpoint is not null}, HasAuthentication={Headers is not null})";
}

internal sealed class ServiceMantleOtlpRuntime
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
    private readonly IReadOnlyList<ServiceMantleOtlpRegistration> registrations;
    private readonly IServiceMantleOtlpAuthenticationHeaderResolver? headerResolver;
    private ServiceMantleOtlpSignalConfiguration? traces;
    private ServiceMantleOtlpSignalConfiguration? metrics;

    public ServiceMantleOtlpRuntime(
        IEnumerable<ServiceMantleOtlpRegistration> registrations,
        IServiceMantleOtlpAuthenticationHeaderResolver? headerResolver = null)
    {
        this.registrations = registrations.ToList().AsReadOnly();
        this.headerResolver = headerResolver;
    }

    internal void Validate()
    {
        _ = Get(ServiceMantleOtlpSignal.Traces);
        _ = Get(ServiceMantleOtlpSignal.Metrics);
    }

    internal ServiceMantleOtlpSignalConfiguration Get(ServiceMantleOtlpSignal signal)
    {
        lock (sync)
        {
            var cached = signal == ServiceMantleOtlpSignal.Traces ? traces : metrics;
            if (cached is not null)
            {
                return cached;
            }

            ServiceMantleOtlpSignalConfiguration? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(
                    signal,
                    signal == ServiceMantleOtlpSignal.Traces
                        ? registration.Traces
                        : registration.Metrics);
                if (baseline is not null && baseline != candidate)
                {
                    throw Failure(
                        "registration",
                        WellKnownServiceMantleOtlpErrorCodes.ConflictingRegistration);
                }

                baseline = candidate;
            }

            baseline ??= Disabled();
            var resolved = baseline.Enabled ? ResolveHeader(signal, baseline) : baseline;
            if (signal == ServiceMantleOtlpSignal.Traces)
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

    private static ServiceMantleOtlpSignalConfiguration Normalize(
        ServiceMantleOtlpSignal signal,
        ServiceMantleOtlpSignalRegistration registration)
    {
        if (!registration.Enabled)
        {
            return Disabled();
        }

        var fieldPrefix = signal == ServiceMantleOtlpSignal.Traces ? "traces" : "metrics";
        if (!Enum.IsDefined(registration.Protocol))
        {
            throw Failure(
                $"{fieldPrefix}.protocol",
                WellKnownServiceMantleOtlpErrorCodes.InvalidProtocol);
        }

        if (registration.EndpointRejected)
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint);
        }

        var endpoint = registration.Endpoint;
        if (endpoint is null)
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownServiceMantleOtlpErrorCodes.EndpointRequired);
        }

        ValidateEndpoint(registration, endpoint, fieldPrefix);
        var exportTimeout = Milliseconds(
            registration.ExportTimeout,
            MinimumExportTimeoutMilliseconds,
            MaximumExportTimeoutMilliseconds,
            $"{fieldPrefix}.exportTimeout",
            WellKnownServiceMantleOtlpErrorCodes.InvalidExportTimeout);
        var batchDelay = Milliseconds(
            registration.BatchDelay,
            MinimumBatchDelayMilliseconds,
            MaximumBatchDelayMilliseconds,
            $"{fieldPrefix}.batchDelay",
            WellKnownServiceMantleOtlpErrorCodes.InvalidBatchDelay);
        var maxQueueSize = 0;
        var maxBatchSize = 0;
        if (signal == ServiceMantleOtlpSignal.Traces)
        {
            maxQueueSize = registration.MaxQueueSize;
            if (maxQueueSize is < MinimumQueueSize or > MaximumQueueSize)
            {
                throw Failure(
                    "traces.maxQueueSize",
                    WellKnownServiceMantleOtlpErrorCodes.InvalidQueueSize);
            }

            maxBatchSize = registration.MaxExportBatchSize;
            if (maxBatchSize is < MinimumBatchSize or > MaximumBatchSize ||
                maxBatchSize > maxQueueSize)
            {
                throw Failure(
                    "traces.maxExportBatchSize",
                    WellKnownServiceMantleOtlpErrorCodes.InvalidBatchSize);
            }
        }

        var headerName = registration.AuthenticationHeaderName?.Trim();
        if (headerName is not null && !IsSafeResolverName(headerName))
        {
            throw Failure(
                $"{fieldPrefix}.authenticationHeaderName",
                WellKnownServiceMantleOtlpErrorCodes.AuthenticationInvalid);
        }

        return new ServiceMantleOtlpSignalConfiguration(
            Enabled: true,
            registration.Protocol == ServiceMantleOtlpProtocol.Grpc
                ? OtlpExportProtocol.Grpc
                : OtlpExportProtocol.HttpProtobuf,
            endpoint,
            headerName,
            exportTimeout,
            batchDelay,
            maxQueueSize,
            maxBatchSize);
    }

    private ServiceMantleOtlpSignalConfiguration ResolveHeader(
        ServiceMantleOtlpSignal signal,
        ServiceMantleOtlpSignalConfiguration configuration)
    {
        var resolverName = configuration.Headers;
        if (resolverName is null)
        {
            return configuration;
        }

        ServiceMantleOtlpAuthenticationHeader? header;
        try
        {
            if (headerResolver is null || !headerResolver.TryResolve(resolverName, out header) || header is null)
            {
                throw Failure(
                    AuthenticationField(signal),
                    WellKnownServiceMantleOtlpErrorCodes.AuthenticationMissing);
            }
        }
        catch (ServiceMantleOtlpConfigurationException)
        {
            throw;
        }
        catch
        {
            throw Failure(
                AuthenticationField(signal),
                WellKnownServiceMantleOtlpErrorCodes.AuthenticationMissing);
        }

        if (!IsHttpToken(header.Name) ||
            string.IsNullOrEmpty(header.Value) ||
            header.Value.Any(character => character is '\r' or '\n'))
        {
            throw Failure(
                AuthenticationField(signal),
                WellKnownServiceMantleOtlpErrorCodes.AuthenticationInvalid);
        }

        return configuration.WithHeaders(
            $"{header.Name}={Uri.EscapeDataString(header.Value)}");
    }

    private static void ValidateEndpoint(
        ServiceMantleOtlpSignalRegistration registration,
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
                    WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint);
            }

            if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    !registration.AllowInsecureLoopbackForTesting ||
                    !endpoint.IsLoopback)
                {
                    throw Failure(
                        $"{fieldPrefix}.endpoint",
                        WellKnownServiceMantleOtlpErrorCodes.InsecureEndpoint);
                }
            }

        }
        catch (ServiceMantleOtlpConfigurationException)
        {
            throw;
        }
        catch
        {
            throw Failure(
                $"{fieldPrefix}.endpoint",
                WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint);
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

    private static string AuthenticationField(ServiceMantleOtlpSignal signal) =>
        signal == ServiceMantleOtlpSignal.Traces
            ? "traces.authenticationHeaderName"
            : "metrics.authenticationHeaderName";

    private static ServiceMantleOtlpSignalConfiguration Disabled() => new(
        Enabled: false,
        OtlpExportProtocol.Grpc,
        Endpoint: null,
        Headers: null,
        ExportTimeoutMilliseconds: 0,
        BatchDelayMilliseconds: 0,
        MaxQueueSize: 0,
        MaxExportBatchSize: 0);

    private static ServiceMantleOtlpConfigurationException Failure(
        string fieldName,
        string errorCode) => new(fieldName, errorCode);
}

internal sealed class ServiceMantleOtlpOptionsConfigurator(ServiceMantleOtlpRuntime runtime)
    : IConfigureNamedOptions<OtlpExporterOptions>
{
    public void Configure(OtlpExporterOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OtlpExporterOptions options)
    {
        ServiceMantleOtlpSignal? signal = name switch
        {
            ServiceMantleOtlpNames.Traces => ServiceMantleOtlpSignal.Traces,
            ServiceMantleOtlpNames.Metrics => ServiceMantleOtlpSignal.Metrics,
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
        if (signal == ServiceMantleOtlpSignal.Traces)
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

internal sealed class ServiceMantleOtlpStartupValidator(ServiceMantleOtlpRuntime runtime)
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

internal static class ServiceMantleOtlpNames
{
    internal const string Traces = "ServiceMantle.Otlp.Traces";
    internal const string Metrics = "ServiceMantle.Otlp.Metrics";
}

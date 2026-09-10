namespace ServiceMantle.OpenTelemetry.Otlp;

/// <summary>Identifies the supported OTLP transport protocols.</summary>
public enum ServiceMantleOtlpProtocol
{
    /// <summary>OTLP over gRPC.</summary>
    Grpc,

    /// <summary>OTLP protobuf payloads over HTTP.</summary>
    HttpProtobuf,
}

/// <summary>Configures settings shared by one explicitly enabled OTLP signal.</summary>
public abstract class ServiceMantleOtlpSignalOptions
{
    private Uri? endpoint;

    /// <summary>Gets or sets whether this signal is exported. The default is false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the OTLP transport protocol.</summary>
    public ServiceMantleOtlpProtocol Protocol { get; set; } = ServiceMantleOtlpProtocol.Grpc;

    /// <summary>
    /// Gets or sets the signal-specific collector endpoint. User-info, query, and fragment
    /// components are rejected and never retained.
    /// </summary>
    public Uri? Endpoint
    {
        get => endpoint;
        set
        {
            EndpointContainedUnsafeComponents = value is not null &&
                (value.OriginalString.Contains('?', StringComparison.Ordinal) ||
                 value.OriginalString.Contains('#', StringComparison.Ordinal) ||
                 (value.IsAbsoluteUri && !string.IsNullOrEmpty(value.UserInfo)));
            endpoint = EndpointContainedUnsafeComponents ? null : value;
        }
    }

    /// <summary>
    /// Gets or sets the non-secret resolver name for an optional authentication header.
    /// </summary>
    public string? AuthenticationHeaderName { get; set; }

    /// <summary>
    /// Gets or sets whether an HTTP endpoint is permitted for explicit loopback-only tests.
    /// </summary>
    public bool AllowInsecureLoopbackForTesting { get; set; }

    /// <summary>Gets or sets the exporter timeout. The supported range is 1–30 seconds.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the batch or collection delay. The supported range is 100 ms–30 seconds.</summary>
    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromSeconds(5);

    internal bool EndpointContainedUnsafeComponents { get; private set; }

    /// <summary>Returns non-sensitive configuration metadata.</summary>
    public override string ToString() =>
        $"{GetType().Name}(Enabled={Enabled}, Protocol={Protocol}, " +
        $"HasEndpoint={Endpoint is not null}, HasAuthentication={AuthenticationHeaderName is not null})";
}

/// <summary>Configures OTLP trace exporting.</summary>
public sealed class ServiceMantleOtlpTraceOptions : ServiceMantleOtlpSignalOptions
{
    /// <summary>Gets or sets the bounded trace queue size. The supported range is 100–50,000.</summary>
    public int MaxQueueSize { get; set; } = 2_048;

    /// <summary>
    /// Gets or sets the maximum trace export batch size. The supported range is 1–1,000 and
    /// cannot exceed <see cref="MaxQueueSize"/>.
    /// </summary>
    public int MaxExportBatchSize { get; set; } = 512;
}

/// <summary>Configures OTLP metric exporting.</summary>
public sealed class ServiceMantleOtlpMetricOptions : ServiceMantleOtlpSignalOptions;

/// <summary>Configures the independently enabled OTLP trace and metric exporters.</summary>
public sealed class ServiceMantleOtlpOptions
{
    /// <summary>Gets trace exporter settings. Tracing is disabled by default.</summary>
    public ServiceMantleOtlpTraceOptions Traces { get; } = new();

    /// <summary>Gets metric exporter settings. Metrics are disabled by default.</summary>
    public ServiceMantleOtlpMetricOptions Metrics { get; } = new();

    /// <summary>Returns only signal enablement metadata.</summary>
    public override string ToString() =>
        $"ServiceMantleOtlpOptions(TracingEnabled={Traces.Enabled}, MetricsEnabled={Metrics.Enabled})";
}

/// <summary>Contains one resolved OTLP authentication header.</summary>
public sealed class ServiceMantleOtlpAuthenticationHeader
{
    /// <summary>Initializes a resolved authentication header.</summary>
    public ServiceMantleOtlpAuthenticationHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
    }

    /// <summary>Gets the HTTP header name.</summary>
    public string Name { get; }

    /// <summary>Gets the secret HTTP header value.</summary>
    public string Value { get; }

    /// <summary>Returns metadata only and never includes the header value.</summary>
    public override string ToString() => "ServiceMantleOtlpAuthenticationHeader(Resolved=True)";
}

/// <summary>Resolves an OTLP authentication header by a non-secret configuration name.</summary>
public interface IServiceMantleOtlpAuthenticationHeaderResolver
{
    /// <summary>Attempts to resolve one authentication header.</summary>
    bool TryResolve(string name, out ServiceMantleOtlpAuthenticationHeader? header);
}

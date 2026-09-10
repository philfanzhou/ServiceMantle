namespace ServiceMantle.OpenTelemetry.Otlp;

/// <summary>Stable error codes for safe OTLP configuration failures.</summary>
public static class WellKnownServiceMantleOtlpErrorCodes
{
    /// <summary>Repeated registrations conflict.</summary>
    public const string ConflictingRegistration = "otlp.configuration_conflict";
    /// <summary>The selected protocol is not supported.</summary>
    public const string InvalidProtocol = "otlp.invalid_protocol";
    /// <summary>An enabled signal has no endpoint.</summary>
    public const string EndpointRequired = "otlp.endpoint_required";
    /// <summary>The endpoint violates OTLP URI rules.</summary>
    public const string InvalidEndpoint = "otlp.invalid_endpoint";
    /// <summary>The endpoint transport is not securely permitted.</summary>
    public const string InsecureEndpoint = "otlp.insecure_endpoint";
    /// <summary>The exporter timeout is outside the supported range.</summary>
    public const string InvalidExportTimeout = "otlp.invalid_export_timeout";
    /// <summary>The batch delay is outside the supported range.</summary>
    public const string InvalidBatchDelay = "otlp.invalid_batch_delay";
    /// <summary>The trace queue size is outside the supported range.</summary>
    public const string InvalidQueueSize = "otlp.invalid_queue_size";
    /// <summary>The trace batch size is outside the supported range.</summary>
    public const string InvalidBatchSize = "otlp.invalid_batch_size";
    /// <summary>The configured authentication header could not be resolved.</summary>
    public const string AuthenticationMissing = "otlp.authentication_missing";
    /// <summary>The resolved authentication header is unsafe or malformed.</summary>
    public const string AuthenticationInvalid = "otlp.authentication_invalid";
}

/// <summary>Indicates a safe OTLP configuration failure.</summary>
public sealed class ServiceMantleOtlpConfigurationException : InvalidOperationException
{
    internal ServiceMantleOtlpConfigurationException(string fieldName, string errorCode)
        : base("The ServiceMantle OTLP exporter configuration is invalid.")
    {
        FieldName = fieldName;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the safe configuration field name.</summary>
    public string FieldName { get; }

    /// <summary>Gets the stable non-sensitive error code.</summary>
    public string ErrorCode { get; }

    /// <summary>Returns only safe failure metadata.</summary>
    public override string ToString() =>
        $"ServiceMantleOtlpConfigurationException(FieldName={FieldName}, ErrorCode={ErrorCode})";
}

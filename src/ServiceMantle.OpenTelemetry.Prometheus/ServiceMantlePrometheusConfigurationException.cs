namespace ServiceMantle.OpenTelemetry.Prometheus;

/// <summary>Defines stable Prometheus endpoint configuration error codes.</summary>
public static class WellKnownServiceMantlePrometheusErrorCodes
{
    /// <summary>The configured endpoint path is invalid.</summary>
    public const string InvalidEndpointPath = "prometheus.invalid_endpoint_path";

    /// <summary>The endpoint path conflicts with a reserved or mapped route.</summary>
    public const string EndpointPathConflict = "prometheus.endpoint_path_conflict";

    /// <summary>An enabled endpoint does not name an authorization policy.</summary>
    public const string AuthorizationPolicyRequired = "prometheus.authorization_policy_required";

    /// <summary>The named authorization policy is not registered.</summary>
    public const string AuthorizationPolicyNotFound = "prometheus.authorization_policy_not_found";

    /// <summary>Multiple registrations do not describe the same effective endpoint.</summary>
    public const string ConflictingRegistration = "prometheus.conflicting_registration";

    /// <summary>The effective exporter options do not preserve the fixed endpoint guarantees.</summary>
    public const string ExporterOptionsConflict = "prometheus.exporter_options_conflict";

    /// <summary>The enabled endpoint was not mapped exactly once.</summary>
    public const string EndpointMappingRequired = "prometheus.endpoint_mapping_required";
}

/// <summary>Indicates a safe startup failure in Prometheus endpoint configuration.</summary>
public sealed class ServiceMantlePrometheusConfigurationException : Exception
{
    internal ServiceMantlePrometheusConfigurationException(string errorCode, string fieldName)
        : base($"The ServiceMantle Prometheus configuration is invalid for {fieldName} ({errorCode}).")
    {
        ErrorCode = errorCode;
        FieldName = fieldName;
    }

    /// <summary>Gets the stable, non-sensitive failure classification.</summary>
    public string ErrorCode { get; }

    /// <summary>Gets the configuration field that failed validation.</summary>
    public string FieldName { get; }

    /// <summary>Returns only stable configuration metadata.</summary>
    public override string ToString() =>
        $"ServiceMantlePrometheusConfigurationException(ErrorCode={ErrorCode}, FieldName={FieldName})";
}

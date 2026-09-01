namespace ServiceMantle.Serilog.GrafanaLoki;

/// <summary>Defines stable Grafana Loki configuration and delivery error codes.</summary>
public static class WellKnownServiceMantleGrafanaLokiErrorCodes
{
    /// <summary>The Loki base endpoint is invalid.</summary>
    public const string InvalidEndpoint = "loki.invalid_endpoint";

    /// <summary>The resolver entry name is invalid.</summary>
    public const string InvalidAuthorizationResolverName = "loki.invalid_authorization_resolver_name";

    /// <summary>The authorization resolver is missing.</summary>
    public const string AuthorizationResolverMissing = "loki.authorization_resolver_missing";

    /// <summary>The authorization value is missing or invalid.</summary>
    public const string AuthorizationValueInvalid = "loki.authorization_value_invalid";

    /// <summary>The authorization resolver failed.</summary>
    public const string AuthorizationResolutionFailed = "loki.authorization_resolution_failed";

    /// <summary>A bounded numeric setting is invalid.</summary>
    public const string InvalidBoundedSetting = "loki.invalid_bounded_setting";

    /// <summary>Multiple registrations conflict.</summary>
    public const string ConflictingRegistration = "loki.conflicting_registration";

    /// <summary>The required ServiceMantle Serilog pipeline is missing.</summary>
    public const string SerilogPipelineMissing = "loki.serilog_pipeline_missing";

    /// <summary>The remote sink could not be created.</summary>
    public const string SinkCreationFailed = "loki.sink_creation_failed";

    /// <summary>A transport operation failed.</summary>
    public const string TransportFailed = "loki.transport_failed";

    /// <summary>Loki returned a non-success response.</summary>
    public const string RemoteResponseFailed = "loki.remote_response_failed";

    /// <summary>The shutdown drain reached its configured timeout.</summary>
    public const string ShutdownDrainTimedOut = "loki.shutdown_drain_timed_out";

    /// <summary>The caller cancelled shutdown draining.</summary>
    public const string ShutdownDrainCancelled = "loki.shutdown_drain_cancelled";
}

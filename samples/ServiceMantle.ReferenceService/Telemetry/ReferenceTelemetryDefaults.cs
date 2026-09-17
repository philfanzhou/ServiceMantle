namespace ServiceMantle.ReferenceService.Telemetry;

/// <summary>Defines the consumer-owned base telemetry wiring constants of this sample.</summary>
public static class ReferenceTelemetryDefaults
{
    /// <summary>The explicit boolean switch that enables the sample's base instrumentation.</summary>
    /// <remarks>
    /// The switch defaults to <c>false</c>. A missing, empty, or unparsable value leaves every
    /// ServiceMantle-owned OpenTelemetry provider unregistered. The value is read before the host
    /// builder is created, is fixed before the host is built, and is never reloaded.
    /// </remarks>
    public const string EnabledKey = "ReferenceService:Telemetry:Enabled";

    /// <summary>The explicit boolean switch that publishes the authoritative phase metric.</summary>
    /// <remarks>
    /// The switch defaults to <c>false</c>. A missing, empty, or unparsable value leaves the
    /// <c>ServiceMetrics</c> publisher unregistered and the health snapshot source wired exactly as
    /// before. The value is read before the host is built, is fixed, and is never reloaded. It can
    /// only be enabled together with the PostgreSQL startup gate, because the authoritative phase
    /// comes from that gate's installation row.
    /// </remarks>
    public const string PhaseMetricsEnabledKey = "ReferenceService:Telemetry:PhaseMetrics:Enabled";

    /// <summary>The explicit boolean switch that maps the authorized Prometheus scrape endpoint.</summary>
    /// <remarks>
    /// The switch defaults to <c>false</c>. A missing, empty, or unparsable value leaves the
    /// endpoint unmapped, so <c>/metrics</c> stays a 404 and no Prometheus-owned service is
    /// registered. The value is read before the host is built, is fixed, and is never reloaded. It
    /// can only be enabled together with the PostgreSQL startup gate, because the scrape is
    /// authorized by the management session that gate registers.
    /// </remarks>
    public const string PrometheusEnabledKey = "ReferenceService:Telemetry:Prometheus:Enabled";
}

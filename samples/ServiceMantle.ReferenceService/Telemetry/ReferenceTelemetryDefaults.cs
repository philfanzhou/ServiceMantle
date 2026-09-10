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
}

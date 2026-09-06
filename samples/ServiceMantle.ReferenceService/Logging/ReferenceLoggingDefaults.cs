namespace ServiceMantle.ReferenceService.Logging;

/// <summary>Defines the consumer-owned logging wiring constants of this sample.</summary>
public static class ReferenceLoggingDefaults
{
    /// <summary>The explicit boolean switch that enables the sample's logging wiring.</summary>
    /// <remarks>
    /// The switch defaults to <c>false</c>. A missing or unparsable value leaves the ServiceMantle
    /// Serilog host unregistered; the value is fixed before the host is built and is never reloaded.
    /// </remarks>
    public const string EnabledKey = "ReferenceService:Logging:Enabled";

    /// <summary>The sample-owned request Header whose values are always redacted.</summary>
    /// <remarks>The built-in denied Header names remain in effect and cannot be removed.</remarks>
    public const string SecretHeaderName = "X-Reference-Secret";

    /// <summary>The logger category of the sample's single request log line.</summary>
    public const string RequestLoggerCategory = "ServiceMantle.ReferenceService.Requests";

    /// <summary>The fixed message template of the sample's request log line.</summary>
    /// <remarks>
    /// The template never interpolates caller data. Only the bounded result classification, the
    /// status code, the matched route pattern, the request method collapsed to the framework's known
    /// token set, and the projected Header graph are attached as structured properties, which the
    /// mandatory sanitizing sink cleans. The Header graph redacts denied Header values in full and
    /// projects the remaining Header values under the free-text contract.
    /// </remarks>
    public const string RequestMessageTemplate =
        "Reference request handled {Result} {Method} {Route} {StatusCode} {@Headers}";
}

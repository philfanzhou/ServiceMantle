using ServiceMantle.Configuration;

namespace ServiceMantle.Diagnostics.Export.Otlp;

/// <summary>The classified <c>opentelemetry.otlp_endpoint</c> setting.</summary>
/// <remarks>
/// The endpoint value is held in memory for the usable case only and is never rendered by
/// <see cref="ToString"/> or the category.
/// </remarks>
public sealed class OtlpSettingState
{
    private OtlpSettingState(Uri? endpoint, bool isUnusable)
    {
        Endpoint = endpoint;
        IsUnusable = isUnusable;
    }

    /// <summary>Gets the endpoint to export through; null keeps the OTLP exporters off.</summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// Gets a value indicating whether a value is stored but cannot be used, so the consumer
    /// should record the fixed startup warning category.
    /// </summary>
    public bool IsUnusable { get; }

    /// <summary>
    /// Gets the fixed, value-free category for the startup warning: <c>enabled</c>,
    /// <c>disabled</c>, or <c>endpoint_invalid</c>.
    /// </summary>
    public string Category => Endpoint is not null ? "enabled" : IsUnusable ? "endpoint_invalid" : "disabled";

    /// <summary>Returns classification metadata only; never the endpoint value.</summary>
    public override string ToString() =>
        $"OtlpSettingState(Category={Category})";

    /// <summary>Classifies the <c>opentelemetry.otlp_endpoint</c> value of a snapshot.</summary>
    /// <param name="snapshot">The activated setting snapshot.</param>
    /// <returns>The classified state. A missing key classifies as disabled.</returns>
    public static OtlpSettingState Classify(ServiceSettingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? endpointText = null;
        if (snapshot.Values.TryGetValue(OtlpSettingDefinitions.Endpoint, out var value) &&
            value.HasValue &&
            value.ValueType == ServiceSettingValueType.String)
        {
            endpointText = value.GetString();
        }

        return Classify(endpointText);
    }

    /// <summary>Classifies a raw endpoint value.</summary>
    /// <param name="value">The stored endpoint text, or null.</param>
    /// <returns>The classified state.</returns>
    public static OtlpSettingState Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new OtlpSettingState(null, isUnusable: false);
        }

        return TryParseEndpoint(value, out var endpoint)
            ? new OtlpSettingState(endpoint, isUnusable: false)
            : new OtlpSettingState(null, isUnusable: true);
    }

    /// <summary>
    /// The one endpoint rule the classification and the OTLP exporter share: an absolute HTTPS URI
    /// with a host and without user info, query, or fragment.
    /// </summary>
    public static bool TryParseEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }
}

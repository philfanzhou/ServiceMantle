using ServiceMantle.Configuration;

namespace ServiceMantle.Logging.Remote;

/// <summary>What the stored Loki setting pair means for this start.</summary>
public enum GrafanaLokiSettingStatus
{
    /// <summary>Both values are empty: remote log shipping is off by choice.</summary>
    Disabled,

    /// <summary>A usable HTTPS endpoint and a usable Authorization value: the sink may be enabled.</summary>
    Enabled,

    /// <summary>The endpoint is not an absolute HTTPS URI without user info, query, or fragment.</summary>
    EndpointInvalid,

    /// <summary>An Authorization value is stored without an endpoint.</summary>
    EndpointMissing,

    /// <summary>An endpoint is stored without a usable Authorization value.</summary>
    AuthorizationMissing,

    /// <summary>The stored Authorization value is not a usable header value.</summary>
    AuthorizationInvalid
}

/// <summary>
/// The classified Loki setting pair. The endpoint and Authorization values are held in memory for
/// the enabled case only and are never rendered by <see cref="ToString"/> or the category.
/// </summary>
public sealed class GrafanaLokiSettingState
{
    private GrafanaLokiSettingState(
        GrafanaLokiSettingStatus status,
        Uri? endpoint,
        string? authorization)
    {
        Status = status;
        Endpoint = endpoint;
        Authorization = authorization;
    }

    /// <summary>Gets the classified status.</summary>
    public GrafanaLokiSettingStatus Status { get; }

    /// <summary>Gets the endpoint; set only when <see cref="Status"/> is <see cref="GrafanaLokiSettingStatus.Enabled"/>.</summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// Gets the Authorization header value; set only when
    /// <see cref="Status"/> is <see cref="GrafanaLokiSettingStatus.Enabled"/>. Never rendered.
    /// </summary>
    public string? Authorization { get; }

    /// <summary>
    /// Gets a value indicating whether stored values exist but cannot be used, so the consumer
    /// should record the fixed startup warning category.
    /// </summary>
    public bool IsUnusable => Status
        is not (GrafanaLokiSettingStatus.Disabled or GrafanaLokiSettingStatus.Enabled);

    /// <summary>Gets the fixed, value-free category for the startup warning.</summary>
    public string Category => Status switch
    {
        GrafanaLokiSettingStatus.EndpointInvalid => "endpoint_invalid",
        GrafanaLokiSettingStatus.EndpointMissing => "endpoint_missing",
        GrafanaLokiSettingStatus.AuthorizationMissing => "authorization_missing",
        GrafanaLokiSettingStatus.AuthorizationInvalid => "authorization_invalid",
        GrafanaLokiSettingStatus.Enabled => "enabled",
        _ => "disabled"
    };

    /// <summary>Returns the category only; never the endpoint or the Authorization value.</summary>
    public override string ToString() => $"GrafanaLokiSettingState({Category})";

    /// <summary>Classifies the <c>loki.uri</c> / <c>loki.authorization</c> pair of a snapshot.</summary>
    /// <param name="snapshot">The activated setting snapshot.</param>
    /// <returns>The classified state. Missing keys classify as <see cref="GrafanaLokiSettingStatus.Disabled"/>.</returns>
    public static GrafanaLokiSettingState Classify(ServiceSettingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? endpointText = null;
        string? authorizationText = null;
        if (snapshot.Values.TryGetValue(GrafanaLokiSettingDefinitions.Endpoint, out var endpointValue) &&
            endpointValue.HasValue &&
            endpointValue.ValueType == ServiceSettingValueType.String)
        {
            endpointText = endpointValue.GetString();
        }

        if (snapshot.Values.TryGetValue(GrafanaLokiSettingDefinitions.Authorization, out var authorizationValue) &&
            authorizationValue.HasValue &&
            authorizationValue.ValueType == ServiceSettingValueType.String)
        {
            authorizationText = authorizationValue.GetString();
        }

        return Classify(endpointText, authorizationText);
    }

    /// <summary>Classifies a raw endpoint / Authorization pair.</summary>
    /// <param name="endpoint">The stored endpoint text, or null.</param>
    /// <param name="authorization">The stored Authorization value, or null.</param>
    /// <returns>The classified state.</returns>
    public static GrafanaLokiSettingState Classify(string? endpoint, string? authorization)
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var hasAuthorization = !string.IsNullOrWhiteSpace(authorization);
        if (!hasEndpoint)
        {
            return new GrafanaLokiSettingState(
                hasAuthorization
                    ? GrafanaLokiSettingStatus.EndpointMissing
                    : GrafanaLokiSettingStatus.Disabled,
                null,
                null);
        }

        if (!TryParseEndpoint(endpoint, out var parsedEndpoint))
        {
            return new GrafanaLokiSettingState(GrafanaLokiSettingStatus.EndpointInvalid, null, null);
        }

        if (!hasAuthorization)
        {
            return new GrafanaLokiSettingState(GrafanaLokiSettingStatus.AuthorizationMissing, null, null);
        }

        return GrafanaLokiSettingDefinitions.IsUsableAuthorization(authorization)
            ? new GrafanaLokiSettingState(
                GrafanaLokiSettingStatus.Enabled, parsedEndpoint, authorization)
            : new GrafanaLokiSettingState(GrafanaLokiSettingStatus.AuthorizationInvalid, null, null);
    }

    /// <summary>
    /// The one endpoint rule the classification and the Grafana Loki sink share: an absolute HTTPS
    /// URI with a host and without user info, query, or fragment.
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

using ServiceMantle.Configuration;

namespace ServiceMantle.Logging.Remote;

/// <summary>What the stored Loki setting pair means for this start.</summary>
public enum GrafanaLokiSettingStatus
{
    /// <summary>Both values are empty: remote log shipping is off by choice.</summary>
    Disabled,

    /// <summary>A usable allowed endpoint and an explicit authentication choice: the sink may be enabled.</summary>
    Enabled,

    /// <summary>The endpoint is not an allowed absolute HTTP(S) URI without user info, query, or fragment.</summary>
    EndpointInvalid,

    /// <summary>An Authorization value is stored without an endpoint.</summary>
    EndpointMissing,

    /// <summary>An endpoint is stored without a usable Authorization value.</summary>
    AuthorizationMissing,

    /// <summary>The stored Authorization value is unusable or conflicts with explicit no authentication.</summary>
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
        string? authorization,
        bool allowInsecureHttp = false)
    {
        Status = status;
        Endpoint = endpoint;
        Authorization = authorization;
        AllowInsecureHttp = allowInsecureHttp;
    }

    /// <summary>Gets the classified status.</summary>
    public GrafanaLokiSettingStatus Status { get; }

    /// <summary>Gets the endpoint; set only when <see cref="Status"/> is <see cref="GrafanaLokiSettingStatus.Enabled"/>.</summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// Gets the Authorization header value; set only for authenticated settings when
    /// <see cref="Status"/> is <see cref="GrafanaLokiSettingStatus.Enabled"/>. Never rendered.
    /// </summary>
    public string? Authorization { get; }

    /// <summary>Gets the explicit HTTP transport policy for registration.</summary>
    public bool AllowInsecureHttp { get; }

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

        snapshot.Values.TryGetValue(GrafanaLokiSettingDefinitions.AllowInsecureHttp, out var httpValue);
        snapshot.Values.TryGetValue(GrafanaLokiSettingDefinitions.AllowNoAuthentication, out var noAuthenticationValue);
        return Classify(endpointText, authorizationText,
            GrafanaLokiSettingDefinitions.BooleanOrFalse(httpValue),
            GrafanaLokiSettingDefinitions.BooleanOrFalse(noAuthenticationValue));
    }

    /// <summary>Classifies a raw endpoint / Authorization pair.</summary>
    /// <param name="endpoint">The stored endpoint text, or null.</param>
    /// <param name="authorization">The stored Authorization value, or null.</param>
    /// <returns>The classified state.</returns>
    public static GrafanaLokiSettingState Classify(string? endpoint, string? authorization) =>
        Classify(endpoint, authorization, false, false);

    /// <summary>Classifies raw settings with explicit HTTP and no-authentication policies.</summary>
    /// <param name="endpoint">The stored endpoint text, or null.</param>
    /// <param name="authorization">The stored Authorization value, or null.</param>
    /// <param name="allowInsecureHttp">Whether HTTP transport is explicitly permitted.</param>
    /// <param name="allowNoAuthentication">Whether no authentication is explicitly selected.</param>
    /// <returns>The classified, value-free state.</returns>
    public static GrafanaLokiSettingState Classify(
        string? endpoint, string? authorization, bool allowInsecureHttp, bool allowNoAuthentication) =>
        Evaluate(endpoint, authorization, allowInsecureHttp, allowNoAuthentication, strict: false).State;

    // One policy evaluation drives both strict complete-candidate updates and tolerant startup.
    // Startup preserves the historical treatment of blank strings as missing; management rejects
    // explicitly stored blanks. No-authentication never silently discards a stored credential.
    internal static (GrafanaLokiSettingState State, IReadOnlyList<ServiceSettingValidationError> Errors) Evaluate(
        string? endpoint, string? authorization, bool allowInsecureHttp, bool allowNoAuthentication, bool strict)
    {
        var hasEndpoint = strict ? endpoint is not null : !string.IsNullOrWhiteSpace(endpoint);
        var hasAuthorization = strict || allowNoAuthentication
            ? authorization is not null : !string.IsNullOrWhiteSpace(authorization);
        var endpointValid = TryParseEndpoint(endpoint, allowInsecureHttp, out var parsedEndpoint);
        var authorizationInvalid = hasAuthorization &&
            (allowNoAuthentication || !GrafanaLokiSettingDefinitions.IsUsableAuthorization(authorization));
        var errors = new List<ServiceSettingValidationError>();
        if (hasEndpoint && !endpointValid)
            errors.Add(new(GrafanaLokiSettingDefinitions.Endpoint, WellKnownGrafanaLokiErrorCodes.InvalidEndpoint));
        if (authorizationInvalid)
            errors.Add(new(GrafanaLokiSettingDefinitions.Authorization, WellKnownGrafanaLokiErrorCodes.AuthorizationValueInvalid));
        if (!hasEndpoint && hasAuthorization)
            errors.Add(new(GrafanaLokiSettingDefinitions.Endpoint, WellKnownServiceSettingValidationErrorCodes.Required));
        if (hasEndpoint && !hasAuthorization && !allowNoAuthentication)
            errors.Add(new(GrafanaLokiSettingDefinitions.Authorization, WellKnownServiceSettingValidationErrorCodes.Required));

        var status = !hasEndpoint
            ? (hasAuthorization ? GrafanaLokiSettingStatus.EndpointMissing : GrafanaLokiSettingStatus.Disabled)
            : !endpointValid ? GrafanaLokiSettingStatus.EndpointInvalid
            : authorizationInvalid ? GrafanaLokiSettingStatus.AuthorizationInvalid
            : !hasAuthorization && !allowNoAuthentication ? GrafanaLokiSettingStatus.AuthorizationMissing
            : GrafanaLokiSettingStatus.Enabled;
        return (new GrafanaLokiSettingState(status,
            status == GrafanaLokiSettingStatus.Enabled ? parsedEndpoint : null,
            status == GrafanaLokiSettingStatus.Enabled && !allowNoAuthentication ? authorization : null,
            allowInsecureHttp), errors);
    }

    /// <summary>
    /// The one endpoint rule the classification and the Grafana Loki sink share: an allowed absolute HTTP(S)
    /// URI with a host and without user info, query, or fragment.
    /// </summary>
    public static bool TryParseEndpoint(string? value, out Uri? endpoint) =>
        TryParseEndpoint(value, false, out endpoint);

    /// <summary>Parses a structurally safe endpoint under the explicit HTTP policy.</summary>
    public static bool TryParseEndpoint(string? value, bool allowInsecureHttp, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps &&
                !(allowInsecureHttp && parsed.Scheme == Uri.UriSchemeHttp)) ||
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

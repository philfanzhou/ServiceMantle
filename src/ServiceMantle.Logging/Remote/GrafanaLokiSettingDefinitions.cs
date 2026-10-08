using ServiceMantle.Configuration;

namespace ServiceMantle.Logging.Remote;

/// <summary>
/// Defines the snapshot-driven Grafana Loki settings and the management-update combination rules
/// for the endpoint, Authorization value, and the explicit no-authentication policy.
/// </summary>
/// <remarks>
/// <para>
/// The combination validation is the strict management-update rule: a saved value must be one the
/// next start can use, so an endpoint that is not an absolute HTTP(S) URI without user info, query,
/// or fragment, an unusable Authorization value, or a half-configured pair is rejected here. The
/// startup classification (<see cref="GrafanaLokiSettingState"/>) is deliberately more tolerant:
/// values an older release already stored disable the sink with a fixed warning category instead
/// of failing the start.
/// </para>
/// <para>
/// Validation errors never contain the endpoint or the Authorization value; they carry key-scoped
/// safe error codes only.
/// </para>
/// </remarks>
public sealed class GrafanaLokiSettingDefinitions
    : IServiceSettingDefinitionProvider, IServiceSettingCompositeValidator
{
    /// <summary>The Loki base endpoint; an absolute HTTP(S) URI.</summary>
    public const string Endpoint = "loki.uri";

    /// <summary>
    /// The Authorization header value for the Loki endpoint. Sensitive: it is stored protected and
    /// never has a plaintext default.
    /// </summary>
    public const string Authorization = "loki.authorization";

    /// <summary>Explicitly selects no authentication; requires removal of Authorization and restart.</summary>
    public const string AllowNoAuthentication = "loki.allow_no_authentication";

    /// <summary>The maximum usable Authorization value length.</summary>
    public const int MaximumAuthorizationLength = 4_096;

    /// <inheritdoc />
    public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
    [
        new(Endpoint, ServiceSettingValueType.String, requiresRestart: true),
        new(Authorization, ServiceSettingValueType.String, isSensitive: true, requiresRestart: true),
        new(AllowNoAuthentication, ServiceSettingValueType.Boolean, defaultValue: "false", requiresRestart: true)
    ];

    /// <inheritdoc />
    public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.TryGetValue(Endpoint, out var endpointValue);
        context.TryGetValue(Authorization, out var authorizationValue);
        context.TryGetValue(AllowNoAuthentication, out var noAuthenticationValue);
        return GrafanaLokiSettingState.Evaluate(
            TextOrNull(endpointValue), TextOrNull(authorizationValue),
            BooleanOrFalse(noAuthenticationValue), strict: true).Errors;
    }

    internal static bool BooleanOrFalse(ServiceSettingValue? value) =>
        value is { HasValue: true, ValueType: ServiceSettingValueType.Boolean } && value.GetBoolean();

    /// <summary>
    /// The one Authorization rule the update validation and the startup classification share: a
    /// non-blank value of at most <see cref="MaximumAuthorizationLength"/> characters without
    /// control characters.
    /// </summary>
    public static bool IsUsableAuthorization(string? value) =>
        value is { Length: >= 1 and <= MaximumAuthorizationLength } &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    private static string? TextOrNull(ServiceSettingValue? value) =>
        value is not null && value.HasValue && value.ValueType == ServiceSettingValueType.String
            ? value.GetString()
            : null;
}

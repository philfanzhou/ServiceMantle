using ServiceMantle.Configuration;

namespace ServiceMantle.Diagnostics.Export.Otlp;

/// <summary>
/// Defines the snapshot-driven OTLP endpoint setting and its strict management-update validation.
/// </summary>
/// <remarks>
/// The combination validation is the strict management-update rule: a stored
/// <c>opentelemetry.otlp_endpoint</c> must be empty or an absolute HTTPS URI without user info,
/// query, or fragment. The startup classification (<see cref="OtlpSettingState"/>) is deliberately
/// more tolerant: a value an older release already stored keeps the exporter off with a fixed
/// warning category instead of failing the start. Errors never contain the endpoint value.
/// </remarks>
public sealed class OtlpSettingDefinitions
    : IServiceSettingDefinitionProvider, IServiceSettingCompositeValidator
{
    /// <summary>
    /// The OTLP exporter endpoint; an absolute HTTPS URI without user info, query, or fragment.
    /// </summary>
    public const string Endpoint = "opentelemetry.otlp_endpoint";

    /// <inheritdoc />
    public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
    [
        new(Endpoint, ServiceSettingValueType.String, requiresRestart: true)
    ];

    /// <inheritdoc />
    public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.TryGetValue(Endpoint, out var value) ||
            !value.HasValue ||
            value.ValueType != ServiceSettingValueType.String)
        {
            yield break;
        }

        var endpointText = value.GetString();
        if (string.IsNullOrWhiteSpace(endpointText) ||
            OtlpSettingState.TryParseEndpoint(endpointText, out _))
        {
            yield break;
        }

        yield return new ServiceSettingValidationError(
            Endpoint, WellKnownOtlpErrorCodes.InvalidEndpoint);
    }
}

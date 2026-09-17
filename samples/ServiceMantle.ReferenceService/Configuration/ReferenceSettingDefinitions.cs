using ServiceMantle.Configuration;

namespace ServiceMantle.ReferenceService.Configuration;

public sealed class ReferenceSettingDefinitions : IServiceSettingDefinitionProvider
{
    public const string DefaultDisplayName = "Reference workspace";

    /// <summary>
    /// The sensitive example key: its value is protected at rest, never read back through any
    /// query projection, and never carries a default - the shared contract forbids sensitive
    /// defaults.
    /// </summary>
    public const string IntegrationTokenKey = "workspace.integration_token";

    public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
    [
        new("workspace.display_name", ServiceSettingValueType.String, isRequired: true,
            defaultValue: DefaultDisplayName, constraints: [new StringLengthSettingConstraint(1, 120)]),
        new("workspace.item_limit", ServiceSettingValueType.Number, defaultValue: "100",
            constraints: [new NumberRangeSettingConstraint(1, 1000)]),
        new(IntegrationTokenKey, ServiceSettingValueType.String, isSensitive: true,
            constraints: [new StringLengthSettingConstraint(1, 512)])
    ];
}

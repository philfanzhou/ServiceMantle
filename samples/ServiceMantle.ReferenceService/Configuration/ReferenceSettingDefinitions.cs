using ServiceMantle.Configuration;

namespace ServiceMantle.ReferenceService.Configuration;

public sealed class ReferenceSettingDefinitions : IServiceSettingDefinitionProvider
{
    public const string DefaultDisplayName = "Reference workspace";
    public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
    [
        new("workspace.display_name", ServiceSettingValueType.String, isRequired: true,
            defaultValue: DefaultDisplayName, constraints: [new StringLengthSettingConstraint(1, 120)]),
        new("workspace.item_limit", ServiceSettingValueType.Number, defaultValue: "100",
            constraints: [new NumberRangeSettingConstraint(1, 1000)])
    ];
}

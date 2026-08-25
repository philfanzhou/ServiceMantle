using System.Text.Json;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class ServiceSettingValidationTests
{
    [Fact]
    public void Validate_materializes_string_number_boolean_json_defaults_and_missing_optional_values()
    {
        var registry = Registry(
            new ServiceSettingDefinition(
                "product.name",
                ServiceSettingValueType.String,
                defaultValue: "Service A"),
            new ServiceSettingDefinition(
                "product.ratio",
                ServiceSettingValueType.Number,
                defaultValue: "1.25"),
            new ServiceSettingDefinition(
                "product.enabled",
                ServiceSettingValueType.Boolean,
                defaultValue: "TRUE"),
            new ServiceSettingDefinition(
                "product.options",
                ServiceSettingValueType.Json,
                defaultValue: "{\"mode\":\"safe\"}"),
            new ServiceSettingDefinition(
                "product.optional",
                ServiceSettingValueType.String));

        var result = registry.Validate(new Dictionary<string, string?>());

        Assert.True(result.IsValid);
        Assert.Equal("Service A", result.Values["product.name"].GetString());
        Assert.Equal(1.25m, result.Values["product.ratio"].GetNumber());
        Assert.True(result.Values["product.enabled"].GetBoolean());
        Assert.Equal("safe", result.Values["product.options"].GetJson().GetProperty("mode").GetString());
        Assert.All(
            result.Values.Where(pair => pair.Key != "product.optional"),
            pair => Assert.True(pair.Value.IsDefault));
        Assert.False(result.Values["product.optional"].HasValue);
        Assert.False(result.Values["product.optional"].IsDefault);
    }

    [Fact]
    public void Explicit_values_override_defaults_and_use_invariant_number_parsing()
    {
        var registry = Registry(
            new ServiceSettingDefinition(
                "product.ratio",
                ServiceSettingValueType.Number,
                defaultValue: "1.25"),
            new ServiceSettingDefinition(
                "product.enabled",
                ServiceSettingValueType.Boolean,
                defaultValue: "false"));

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["PRODUCT.RATIO"] = "2.5e1",
            ["product.enabled"] = "True"
        });

        Assert.True(result.IsValid);
        Assert.Equal(25m, result.Values["product.ratio"].GetNumber());
        Assert.True(result.Values["product.enabled"].GetBoolean());
        Assert.All(result.Values.Values, value => Assert.False(value.IsDefault));
    }

    [Theory]
    [InlineData(ServiceSettingValueType.Number, "1,25", WellKnownServiceSettingValidationErrorCodes.InvalidNumber)]
    [InlineData(ServiceSettingValueType.Boolean, "yes", WellKnownServiceSettingValidationErrorCodes.InvalidBoolean)]
    [InlineData(ServiceSettingValueType.Json, "{invalid", WellKnownServiceSettingValidationErrorCodes.InvalidJson)]
    [InlineData(ServiceSettingValueType.Json, "{/* comment */}", WellKnownServiceSettingValidationErrorCodes.InvalidJson)]
    [InlineData(ServiceSettingValueType.Json, "{\"trailing\":true,}", WellKnownServiceSettingValidationErrorCodes.InvalidJson)]
    public void Invalid_typed_values_fail_without_partial_materialization(
        ServiceSettingValueType valueType,
        string rawValue,
        string expectedErrorCode)
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.value", valueType),
            new ServiceSettingDefinition(
                "product.valid",
                ServiceSettingValueType.String,
                defaultValue: "valid"));

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.value"] = rawValue
        });

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        var error = Assert.Single(result.Errors);
        Assert.Equal("product.value", error.Key);
        Assert.Equal(expectedErrorCode, error.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Required_values_reject_missing_null_empty_and_whitespace(string? rawValue)
    {
        var registry = Registry(
            new ServiceSettingDefinition(
                "product.required",
                ServiceSettingValueType.String,
                isRequired: true));
        var values = rawValue is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["product.required"] = rawValue };

        var result = registry.Validate(values);

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Equal(
            WellKnownServiceSettingValidationErrorCodes.Required,
            Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Built_in_constraints_validate_string_number_and_json_values()
    {
        var registry = Registry(
            new ServiceSettingDefinition(
                "product.name",
                ServiceSettingValueType.String,
                constraints: [new StringLengthSettingConstraint(3, 8)]),
            new ServiceSettingDefinition(
                "product.retries",
                ServiceSettingValueType.Number,
                constraints: [new NumberRangeSettingConstraint(1, 5)]),
            new ServiceSettingDefinition(
                "product.options",
                ServiceSettingValueType.Json,
                constraints:
                [
                    new JsonRootKindSettingConstraint(
                        [JsonValueKind.Object, JsonValueKind.Array])
                ]));

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.name"] = "too-long-name",
            ["product.retries"] = "6",
            ["product.options"] = "true"
        });

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Collection(
            result.Errors,
            error => Assert.Equal("setting.string_length", error.ErrorCode),
            error => Assert.Equal("setting.json_root_kind", error.ErrorCode),
            error => Assert.Equal("setting.number_range", error.ErrorCode));
    }

    [Fact]
    public void Unknown_invalid_and_duplicate_input_keys_fail_safely()
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.mode", ServiceSettingValueType.String));
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["product.mode"] = "safe",
            ["PRODUCT.MODE"] = "unsafe",
            ["product.unknown"] = "secret-like-value",
            ["invalid/key"] = "another-secret-like-value"
        };

        var result = registry.Validate(values);

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == WellKnownServiceSettingValidationErrorCodes.Duplicate);
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == WellKnownServiceSettingValidationErrorCodes.Unknown);
        var invalidKeyError = Assert.Single(
            result.Errors,
            error => error.ErrorCode == WellKnownServiceSettingValidationErrorCodes.InvalidKey);
        Assert.Null(invalidKeyError.Key);
    }

    [Fact]
    public void Typed_accessors_reject_missing_or_mismatched_access()
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.optional", ServiceSettingValueType.String),
            new ServiceSettingDefinition(
                "product.enabled",
                ServiceSettingValueType.Boolean,
                defaultValue: "true"));
        var result = registry.Validate(new Dictionary<string, string?>());

        Assert.Throws<InvalidOperationException>(() =>
            result.Values["product.optional"].GetString());
        Assert.Throws<InvalidOperationException>(() =>
            result.Values["product.enabled"].GetString());
    }

    private static ServiceSettingDefinitionRegistry Registry(
        params ServiceSettingDefinition[] definitions) =>
        new([new DefinitionProvider(definitions)]);

    private sealed class DefinitionProvider(params ServiceSettingDefinition[] definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }
}

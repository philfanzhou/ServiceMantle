using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class ServiceSettingDefinitionRegistryTests
{
    [Fact]
    public void Empty_registry_contains_no_product_owned_keys()
    {
        var registry = new ServiceSettingDefinitionRegistry();

        Assert.Empty(registry.Definitions);
    }

    [Fact]
    public void Registry_collects_normalizes_and_sorts_consumer_definitions()
    {
        var registry = Registry(
            new ServiceSettingDefinition(" Product.Zeta ", ServiceSettingValueType.String),
            new ServiceSettingDefinition("product.alpha", ServiceSettingValueType.Boolean));

        Assert.Collection(
            registry.Definitions,
            definition => Assert.Equal("product.alpha", definition.Key),
            definition => Assert.Equal("product.zeta", definition.Key));
        Assert.True(registry.TryGetDefinition("PRODUCT.ALPHA", out var alpha));
        Assert.Equal(ServiceSettingValueType.Boolean, alpha!.ValueType);
        Assert.False(registry.TryGetDefinition("invalid/key", out _));
    }

    [Fact]
    public void Registry_rejects_duplicate_keys_case_insensitively()
    {
        var exception = Assert.Throws<ServiceSettingDefinitionException>(() =>
            new ServiceSettingDefinitionRegistry(
            [
                new DefinitionProvider(
                    new ServiceSettingDefinition("product.mode", ServiceSettingValueType.String)),
                new DefinitionProvider(
                    new ServiceSettingDefinition("PRODUCT.MODE", ServiceSettingValueType.String))
            ]));

        Assert.Equal("product.mode", exception.Key);
        Assert.Equal("setting.definition.duplicate_key", exception.ErrorCode);
    }

    [Fact]
    public void Definition_rejects_unsafe_keys_without_echoing_them()
    {
        const string unsafeKey = "password=super-secret";

        var exception = Assert.Throws<ServiceSettingDefinitionException>(() =>
            new ServiceSettingDefinition(unsafeKey, ServiceSettingValueType.String));

        Assert.Null(exception.Key);
        Assert.DoesNotContain(unsafeKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_rejects_sensitive_defaults_without_echoing_them()
    {
        const string secret = "top-secret-default";

        var exception = Assert.Throws<ServiceSettingDefinitionException>(() =>
            new ServiceSettingDefinition(
                "product.token",
                ServiceSettingValueType.String,
                isSensitive: true,
                defaultValue: secret));

        Assert.Equal("setting.definition.sensitive_default", exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_rejects_invalid_defaults_and_constraints_safely()
    {
        const string invalidDefault = "not-a-number-secret-shape";
        var definition = new ServiceSettingDefinition(
            "product.timeout",
            ServiceSettingValueType.Number,
            defaultValue: invalidDefault);

        var exception = Assert.Throws<ServiceSettingDefinitionException>(() => Registry(definition));

        Assert.Equal("setting.definition.invalid_default", exception.ErrorCode);
        Assert.DoesNotContain(invalidDefault, exception.Message, StringComparison.Ordinal);

        Assert.Throws<ServiceSettingDefinitionException>(() =>
            new ServiceSettingDefinition(
                "product.enabled",
                ServiceSettingValueType.Boolean,
                constraints: [new StringLengthSettingConstraint(maximumLength: 10)]));
    }

    [Fact]
    public void Provider_failures_are_reclassified_without_exposing_provider_details()
    {
        const string secret = "provider-secret";

        var exception = Assert.Throws<ServiceSettingDefinitionException>(() =>
            new ServiceSettingDefinitionRegistry([new ThrowingProvider(secret)]));

        Assert.Equal("setting.definition.provider_failed", exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Definition_to_string_contains_metadata_but_not_default_value()
    {
        const string defaultValue = "ordinary-but-not-for-diagnostics";
        var definition = new ServiceSettingDefinition(
            "product.region",
            ServiceSettingValueType.String,
            defaultValue: defaultValue);

        var text = definition.ToString();

        Assert.Contains("product.region", text, StringComparison.Ordinal);
        Assert.DoesNotContain(defaultValue, text, StringComparison.Ordinal);
    }

    private static ServiceSettingDefinitionRegistry Registry(
        params ServiceSettingDefinition[] definitions) =>
        new([new DefinitionProvider(definitions)]);

    private sealed class DefinitionProvider(params ServiceSettingDefinition[] definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }

    private sealed class ThrowingProvider(string secret) : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
            throw new InvalidOperationException(secret);
    }
}

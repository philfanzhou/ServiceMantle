using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class ServiceSettingCompositeAndSecurityTests
{
    [Fact]
    public void Composite_validator_receives_complete_typed_candidate_and_can_reject_a_combination()
    {
        var definitions = new DefinitionProvider(
            new ServiceSettingDefinition(
                "product.remote.enabled",
                ServiceSettingValueType.Boolean,
                defaultValue: "false"),
            new ServiceSettingDefinition(
                "product.remote.endpoint",
                ServiceSettingValueType.String));
        var registry = new ServiceSettingDefinitionRegistry(
            [definitions],
            [new RemoteEndpointValidator()]);

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.remote.enabled"] = "true"
        });

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        var error = Assert.Single(result.Errors);
        Assert.Equal("product.remote.endpoint", error.Key);
        Assert.Equal("setting.remote_endpoint_required", error.ErrorCode);
    }

    [Fact]
    public void Composite_validator_runs_only_after_every_value_is_valid()
    {
        var validator = new CountingValidator();
        var registry = new ServiceSettingDefinitionRegistry(
            [
                new DefinitionProvider(
                    new ServiceSettingDefinition(
                        "product.enabled",
                        ServiceSettingValueType.Boolean))
            ],
            [validator]);

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.enabled"] = "not-boolean"
        });

        Assert.False(result.IsValid);
        Assert.Equal(0, validator.CallCount);
    }

    [Fact]
    public void Constraint_and_composite_exceptions_cannot_expose_sensitive_plaintext()
    {
        const string secret = "very-sensitive-token";
        var definition = new ServiceSettingDefinition(
            "product.token",
            ServiceSettingValueType.String,
            isRequired: true,
            isSensitive: true,
            constraints: [new ThrowingSensitiveConstraint()]);
        var registry = new ServiceSettingDefinitionRegistry(
            [new DefinitionProvider(definition)],
            [new ThrowingCompositeValidator(secret)]);

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.token"] = secret
        });

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.All(
            result.Errors,
            error => Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == WellKnownServiceSettingValidationErrorCodes.ConstraintFailed);
    }

    [Fact]
    public void Successful_sensitive_value_is_available_to_the_consumer_but_never_to_diagnostics()
    {
        const string secret = "very-sensitive-token";
        var definition = new ServiceSettingDefinition(
            "product.token",
            ServiceSettingValueType.String,
            isRequired: true,
            isSensitive: true);
        var registry = new ServiceSettingDefinitionRegistry(
            [new DefinitionProvider(definition)]);

        var result = registry.Validate(new Dictionary<string, string?>
        {
            ["product.token"] = secret
        });

        Assert.True(result.IsValid);
        var value = result.Values["product.token"];
        Assert.Equal(secret, value.GetString());
        Assert.True(value.IsSensitive);
        Assert.DoesNotContain(secret, value.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Throwing_composite_validator_returns_one_stable_catalog_error()
    {
        const string secret = "validator-internal-secret";
        var registry = new ServiceSettingDefinitionRegistry(
            [
                new DefinitionProvider(
                    new ServiceSettingDefinition(
                        "product.enabled",
                        ServiceSettingValueType.Boolean,
                        defaultValue: "true"))
            ],
            [new ThrowingCompositeValidator(secret)]);

        var result = registry.Validate(new Dictionary<string, string?>());

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        var error = Assert.Single(result.Errors);
        Assert.Null(error.Key);
        Assert.Equal(
            WellKnownServiceSettingValidationErrorCodes.CompositeValidationFailed,
            error.ErrorCode);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Composite_validator_cannot_attach_errors_to_unregistered_keys()
    {
        var registry = new ServiceSettingDefinitionRegistry(
            [
                new DefinitionProvider(
                    new ServiceSettingDefinition(
                        "product.enabled",
                        ServiceSettingValueType.Boolean,
                        defaultValue: "true"))
            ],
            [new UnknownKeyValidator()]);

        var result = registry.Validate(new Dictionary<string, string?>());

        var error = Assert.Single(result.Errors);
        Assert.Null(error.Key);
        Assert.Equal(
            WellKnownServiceSettingValidationErrorCodes.CompositeValidationFailed,
            error.ErrorCode);
    }

    [Fact]
    public void Registry_supports_concurrent_validation_without_cross_candidate_state()
    {
        var registry = new ServiceSettingDefinitionRegistry(
            [
                new DefinitionProvider(
                    new ServiceSettingDefinition(
                        "product.sequence",
                        ServiceSettingValueType.Number,
                        constraints: [new NumberRangeSettingConstraint(0, 999)]),
                    new ServiceSettingDefinition(
                        "product.document",
                        ServiceSettingValueType.Json))
            ]);

        Parallel.For(0, 500, index =>
        {
            var result = registry.Validate(new Dictionary<string, string?>
            {
                ["product.sequence"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["product.document"] = $"{{\"sequence\":{index}}}"
            });

            Assert.True(result.IsValid);
            Assert.Equal(index, result.Values["product.sequence"].GetNumber());
            Assert.Equal(index, result.Values["product.document"].GetJson().GetProperty("sequence").GetInt32());
        });
    }

    private sealed class DefinitionProvider(params ServiceSettingDefinition[] definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }

    private sealed class RemoteEndpointValidator : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(
            ServiceSettingValidationContext context)
        {
            context.TryGetValue("product.remote.enabled", out var enabled);
            context.TryGetValue("product.remote.endpoint", out var endpoint);

            if (enabled!.GetBoolean() && !endpoint!.HasValue)
            {
                return
                [
                    new ServiceSettingValidationError(
                        "product.remote.endpoint",
                        "setting.remote_endpoint_required")
                ];
            }

            return [];
        }
    }

    private sealed class CountingValidator : IServiceSettingCompositeValidator
    {
        public int CallCount { get; private set; }

        public IEnumerable<ServiceSettingValidationError> Validate(
            ServiceSettingValidationContext context)
        {
            CallCount++;
            return [];
        }
    }

    private sealed class ThrowingSensitiveConstraint : IServiceSettingValueConstraint
    {
        public ServiceSettingValueType ValueType => ServiceSettingValueType.String;

        public string ErrorCode => "setting.token_invalid";

        public bool IsSatisfied(ServiceSettingValue value) =>
            throw new InvalidOperationException(value.GetString());
    }

    private sealed class ThrowingCompositeValidator(string secret)
        : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(
            ServiceSettingValidationContext context) =>
            throw new InvalidOperationException(secret);
    }

    private sealed class UnknownKeyValidator : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(
            ServiceSettingValidationContext context) =>
        [
            new ServiceSettingValidationError(
                "product.not_registered",
                "setting.invalid_combination")
        ];
    }
}

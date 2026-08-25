using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace ServiceMantle.Configuration;

/// <summary>
/// Holds an immutable product setting catalog and validates complete raw candidates.
/// </summary>
public sealed class ServiceSettingDefinitionRegistry
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private readonly IReadOnlyDictionary<string, ServiceSettingDefinition> definitionsByKey;
    private readonly IReadOnlyList<IServiceSettingCompositeValidator> compositeValidators;

    /// <summary>
    /// Initializes a registry from product-owned definition providers and optional composite validators.
    /// </summary>
    /// <exception cref="ServiceSettingDefinitionException">
    /// A provider, definition, default, constraint, or duplicate key is invalid.
    /// </exception>
    public ServiceSettingDefinitionRegistry(
        IEnumerable<IServiceSettingDefinitionProvider>? providers = null,
        IEnumerable<IServiceSettingCompositeValidator>? compositeValidators = null)
    {
        var definitions = MaterializeDefinitions(providers);
        Definitions = definitions.Values
            .OrderBy(definition => definition.Key, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        definitionsByKey = new ReadOnlyDictionary<string, ServiceSettingDefinition>(definitions);
        this.compositeValidators = MaterializeCompositeValidators(compositeValidators);
    }

    /// <summary>Gets all definitions sorted by normalized key.</summary>
    public IReadOnlyList<ServiceSettingDefinition> Definitions { get; }

    /// <summary>Finds a definition by case-insensitive key.</summary>
    public bool TryGetDefinition(string key, out ServiceSettingDefinition? definition)
    {
        try
        {
            var normalizedKey = ServiceSettingValidationPrimitives.NormalizeKey(key);
            return definitionsByKey.TryGetValue(normalizedKey, out definition);
        }
        catch
        {
            definition = null;
            return false;
        }
    }

    /// <summary>
    /// Parses defaults and explicit raw values, applies every single-value constraint, and finally
    /// runs product-owned combination validators. Any failure returns no partial materialized values.
    /// </summary>
    public ServiceSettingValidationResult Validate(
        IReadOnlyDictionary<string, string?> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);

        var errors = new List<ServiceSettingValidationError>();
        var normalizedRawValues = NormalizeRawValues(rawValues, errors);
        var materializedValues = new Dictionary<string, ServiceSettingValue>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var definition in Definitions)
        {
            var hasExplicitValue = normalizedRawValues.TryGetValue(definition.Key, out var rawValue);
            var isDefault = false;

            if (!hasExplicitValue || rawValue is null)
            {
                if (definition.DefaultValue is not null)
                {
                    rawValue = definition.DefaultValue;
                    isDefault = true;
                }
                else if (definition.IsRequired)
                {
                    errors.Add(new ServiceSettingValidationError(
                        definition.Key,
                        WellKnownServiceSettingValidationErrorCodes.Required));
                    continue;
                }
                else
                {
                    materializedValues.Add(
                        definition.Key,
                        new ServiceSettingValue(definition, hasValue: false, isDefault: false, value: null));
                    continue;
                }
            }

            if (definition.IsRequired && string.IsNullOrWhiteSpace(rawValue))
            {
                errors.Add(new ServiceSettingValidationError(
                    definition.Key,
                    WellKnownServiceSettingValidationErrorCodes.Required));
                continue;
            }

            if (!TryParseValue(definition, rawValue, isDefault, out var value, out var parseErrorCode))
            {
                errors.Add(new ServiceSettingValidationError(definition.Key, parseErrorCode!));
                continue;
            }

            if (!ValidateConstraints(definition, value!, errors))
            {
                continue;
            }

            materializedValues.Add(definition.Key, value!);
        }

        if (errors.Count != 0)
        {
            return Failure(errors);
        }

        var readOnlyValues = new ReadOnlyDictionary<string, ServiceSettingValue>(materializedValues);
        var context = new ServiceSettingValidationContext(readOnlyValues);

        foreach (var validator in compositeValidators)
        {
            try
            {
                var validatorErrors = validator.Validate(context);
                if (validatorErrors is null)
                {
                    throw new InvalidOperationException();
                }

                foreach (var error in validatorErrors)
                {
                    if (error is null)
                    {
                        throw new InvalidOperationException();
                    }

                    if (error.Key is not null && !definitionsByKey.ContainsKey(error.Key))
                    {
                        throw new InvalidOperationException();
                    }

                    errors.Add(error);
                }
            }
            catch
            {
                errors.Add(new ServiceSettingValidationError(
                    key: null,
                    WellKnownServiceSettingValidationErrorCodes.CompositeValidationFailed));
            }
        }

        return errors.Count == 0
            ? new ServiceSettingValidationResult(readOnlyValues, errors.AsReadOnly())
            : Failure(errors);
    }

    private static Dictionary<string, ServiceSettingDefinition> MaterializeDefinitions(
        IEnumerable<IServiceSettingDefinitionProvider>? providers)
    {
        var definitions = new Dictionary<string, ServiceSettingDefinition>(
            StringComparer.OrdinalIgnoreCase);

        if (providers is null)
        {
            return definitions;
        }

        try
        {
            foreach (var provider in providers)
            {
                if (provider is null)
                {
                    throw new ServiceSettingDefinitionException(
                        key: null,
                        "setting.definition.invalid_provider");
                }

                var providerDefinitions = provider.GetDefinitions();
                if (providerDefinitions is null)
                {
                    throw new ServiceSettingDefinitionException(
                        key: null,
                        "setting.definition.invalid_provider");
                }

                foreach (var definition in providerDefinitions)
                {
                    if (definition is null)
                    {
                        throw new ServiceSettingDefinitionException(
                            key: null,
                            "setting.definition.invalid_provider");
                    }

                    if (!definitions.TryAdd(definition.Key, definition))
                    {
                        throw new ServiceSettingDefinitionException(
                            definition.Key,
                            "setting.definition.duplicate_key");
                    }

                    ValidateDefault(definition);
                }
            }
        }
        catch (ServiceSettingDefinitionException)
        {
            throw;
        }
        catch
        {
            throw new ServiceSettingDefinitionException(
                key: null,
                "setting.definition.provider_failed");
        }

        return definitions;
    }

    private static IReadOnlyList<IServiceSettingCompositeValidator> MaterializeCompositeValidators(
        IEnumerable<IServiceSettingCompositeValidator>? validators)
    {
        if (validators is null)
        {
            return [];
        }

        try
        {
            var materialized = new List<IServiceSettingCompositeValidator>();
            foreach (var validator in validators)
            {
                if (validator is null)
                {
                    throw new ServiceSettingDefinitionException(
                        key: null,
                        "setting.definition.invalid_composite_validator");
                }

                materialized.Add(validator);
            }

            return materialized.AsReadOnly();
        }
        catch (ServiceSettingDefinitionException)
        {
            throw;
        }
        catch
        {
            throw new ServiceSettingDefinitionException(
                key: null,
                "setting.definition.invalid_composite_validator");
        }
    }

    private Dictionary<string, string?> NormalizeRawValues(
        IReadOnlyDictionary<string, string?> rawValues,
        ICollection<ServiceSettingValidationError> errors)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in rawValues)
        {
            string normalizedKey;
            try
            {
                normalizedKey = ServiceSettingValidationPrimitives.NormalizeKey(key);
            }
            catch
            {
                errors.Add(new ServiceSettingValidationError(
                    key: null,
                    WellKnownServiceSettingValidationErrorCodes.InvalidKey));
                continue;
            }

            if (!definitionsByKey.ContainsKey(normalizedKey))
            {
                errors.Add(new ServiceSettingValidationError(
                    normalizedKey,
                    WellKnownServiceSettingValidationErrorCodes.Unknown));
                continue;
            }

            if (!normalized.TryAdd(normalizedKey, rawValue))
            {
                errors.Add(new ServiceSettingValidationError(
                    normalizedKey,
                    WellKnownServiceSettingValidationErrorCodes.Duplicate));
            }
        }
        return normalized;
    }

    private static bool TryParseValue(
        ServiceSettingDefinition definition,
        string rawValue,
        bool isDefault,
        out ServiceSettingValue? value,
        out string? errorCode)
    {
        object? parsedValue;
        errorCode = null;

        switch (definition.ValueType)
        {
            case ServiceSettingValueType.String:
                parsedValue = rawValue;
                break;

            case ServiceSettingValueType.Number:
                if (!decimal.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    value = null;
                    errorCode = WellKnownServiceSettingValidationErrorCodes.InvalidNumber;
                    return false;
                }

                parsedValue = number;
                break;

            case ServiceSettingValueType.Boolean:
                if (!bool.TryParse(rawValue, out var boolean))
                {
                    value = null;
                    errorCode = WellKnownServiceSettingValidationErrorCodes.InvalidBoolean;
                    return false;
                }

                parsedValue = boolean;
                break;

            case ServiceSettingValueType.Json:
                try
                {
                    using var document = JsonDocument.Parse(rawValue, JsonOptions);
                    parsedValue = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    value = null;
                    errorCode = WellKnownServiceSettingValidationErrorCodes.InvalidJson;
                    return false;
                }

                break;

            default:
                value = null;
                errorCode = WellKnownServiceSettingValidationErrorCodes.ConstraintFailed;
                return false;
        }

        value = new ServiceSettingValue(
            definition,
            hasValue: true,
            isDefault,
            parsedValue);
        return true;
    }

    private static bool ValidateConstraints(
        ServiceSettingDefinition definition,
        ServiceSettingValue value,
        ICollection<ServiceSettingValidationError> errors)
    {
        var isValid = true;
        foreach (var constraint in definition.Constraints)
        {
            try
            {
                var errorCode = constraint.ErrorCode;
                ServiceSettingValidationPrimitives.ValidateErrorCode(errorCode);
                if (!constraint.IsSatisfied(value))
                {
                    errors.Add(new ServiceSettingValidationError(definition.Key, errorCode));
                    isValid = false;
                }
            }
            catch
            {
                errors.Add(new ServiceSettingValidationError(
                    definition.Key,
                    WellKnownServiceSettingValidationErrorCodes.ConstraintFailed));
                isValid = false;
            }
        }

        return isValid;
    }

    private static void ValidateDefault(ServiceSettingDefinition definition)
    {
        if (definition.DefaultValue is null)
        {
            return;
        }

        if (definition.IsRequired && string.IsNullOrWhiteSpace(definition.DefaultValue))
        {
            throw new ServiceSettingDefinitionException(
                definition.Key,
                "setting.definition.invalid_default");
        }

        if (!TryParseValue(
                definition,
                definition.DefaultValue,
                isDefault: true,
                out var value,
                out _))
        {
            throw new ServiceSettingDefinitionException(
                definition.Key,
                "setting.definition.invalid_default");
        }

        var errors = new List<ServiceSettingValidationError>();
        if (!ValidateConstraints(definition, value!, errors))
        {
            throw new ServiceSettingDefinitionException(
                definition.Key,
                "setting.definition.invalid_default");
        }
    }

    private static ServiceSettingValidationResult Failure(
        List<ServiceSettingValidationError> errors) =>
        new(values: null, errors.AsReadOnly());
}

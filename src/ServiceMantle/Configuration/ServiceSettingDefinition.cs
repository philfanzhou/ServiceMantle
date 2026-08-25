namespace ServiceMantle.Configuration;

/// <summary>
/// Declares one product-owned service setting without providing persistence behavior.
/// </summary>
public sealed class ServiceSettingDefinition
{
    /// <summary>
    /// Initializes a setting definition.
    /// </summary>
    /// <param name="key">The stable product-owned setting key.</param>
    /// <param name="valueType">The value representation.</param>
    /// <param name="isRequired">Whether a value must be present after defaults are applied.</param>
    /// <param name="isSensitive">Whether the materialized value contains secret data.</param>
    /// <param name="defaultValue">An optional persisted-format default value.</param>
    /// <param name="requiresRestart">Whether changing the setting requires a service restart.</param>
    /// <param name="constraints">Optional type-specific value constraints.</param>
    /// <exception cref="ServiceSettingDefinitionException">The definition is unsafe or structurally invalid.</exception>
    public ServiceSettingDefinition(
        string key,
        ServiceSettingValueType valueType,
        bool isRequired = false,
        bool isSensitive = false,
        string? defaultValue = null,
        bool requiresRestart = false,
        IEnumerable<IServiceSettingValueConstraint>? constraints = null)
    {
        Key = ServiceSettingValidationPrimitives.NormalizeKey(key);

        if (!Enum.IsDefined(valueType))
        {
            throw new ServiceSettingDefinitionException(Key, "setting.definition.invalid_type");
        }

        if (isSensitive && defaultValue is not null)
        {
            throw new ServiceSettingDefinitionException(Key, "setting.definition.sensitive_default");
        }

        ValueType = valueType;
        IsRequired = isRequired;
        IsSensitive = isSensitive;
        DefaultValue = defaultValue;
        RequiresRestart = requiresRestart;
        Constraints = MaterializeConstraints(constraints);
    }

    /// <summary>Gets the normalized, case-insensitive key.</summary>
    public string Key { get; }

    /// <summary>Gets the value representation.</summary>
    public ServiceSettingValueType ValueType { get; }

    /// <summary>Gets a value indicating whether a value is required.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets a value indicating whether the materialized value is sensitive.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets the optional persisted-format default. Sensitive definitions cannot have defaults.</summary>
    public string? DefaultValue { get; }

    /// <summary>Gets a value indicating whether changing the setting requires a restart.</summary>
    public bool RequiresRestart { get; }

    /// <summary>Gets the immutable type-specific constraints.</summary>
    public IReadOnlyList<IServiceSettingValueConstraint> Constraints { get; }

    /// <summary>
    /// Returns metadata only and never includes the default value.
    /// </summary>
    public override string ToString() =>
        $"ServiceSettingDefinition(Key={Key}, ValueType={ValueType}, " +
        $"IsRequired={IsRequired}, IsSensitive={IsSensitive}, RequiresRestart={RequiresRestart})";

    private IReadOnlyList<IServiceSettingValueConstraint> MaterializeConstraints(
        IEnumerable<IServiceSettingValueConstraint>? constraints)
    {
        if (constraints is null)
        {
            return [];
        }

        try
        {
            var materialized = new List<IServiceSettingValueConstraint>();
            foreach (var constraint in constraints)
            {
                if (constraint is null ||
                    !Enum.IsDefined(constraint.ValueType) ||
                    constraint.ValueType != ValueType)
                {
                    throw new ServiceSettingDefinitionException(
                        Key,
                        "setting.definition.invalid_constraint");
                }

                ServiceSettingValidationPrimitives.ValidateErrorCode(constraint.ErrorCode);
                materialized.Add(constraint);
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
                Key,
                "setting.definition.invalid_constraint");
        }
    }
}

/// <summary>
/// Supplies product-owned setting definitions to a catalog.
/// </summary>
public interface IServiceSettingDefinitionProvider
{
    /// <summary>
    /// Gets all definitions owned by this provider.
    /// </summary>
    IEnumerable<ServiceSettingDefinition> GetDefinitions();
}

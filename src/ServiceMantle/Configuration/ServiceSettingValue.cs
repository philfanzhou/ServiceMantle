using System.Text.Json;

namespace ServiceMantle.Configuration;

/// <summary>
/// Contains one parsed setting value and safe source metadata.
/// </summary>
public sealed class ServiceSettingValue
{
    private readonly object? value;

    internal ServiceSettingValue(
        ServiceSettingDefinition definition,
        bool hasValue,
        bool isDefault,
        object? value)
    {
        Definition = definition;
        HasValue = hasValue;
        IsDefault = isDefault;
        this.value = value;
    }

    /// <summary>Gets the setting definition.</summary>
    public ServiceSettingDefinition Definition { get; }

    /// <summary>Gets the normalized key.</summary>
    public string Key => Definition.Key;

    /// <summary>Gets the value type.</summary>
    public ServiceSettingValueType ValueType => Definition.ValueType;

    /// <summary>Gets a value indicating whether a value is present.</summary>
    public bool HasValue { get; }

    /// <summary>Gets a value indicating whether the value came from the definition default.</summary>
    public bool IsDefault { get; }

    /// <summary>Gets a value indicating whether the materialized value is sensitive.</summary>
    public bool IsSensitive => Definition.IsSensitive;

    /// <summary>Gets the string value.</summary>
    public string GetString() => GetValue<string>(ServiceSettingValueType.String);

    /// <summary>Gets the decimal number value.</summary>
    public decimal GetNumber() => GetValue<decimal>(ServiceSettingValueType.Number);

    /// <summary>Gets the Boolean value.</summary>
    public bool GetBoolean() => GetValue<bool>(ServiceSettingValueType.Boolean);

    /// <summary>Gets an independent clone of the JSON value.</summary>
    public JsonElement GetJson() => GetValue<JsonElement>(ServiceSettingValueType.Json).Clone();

    /// <summary>
    /// Returns metadata only and never includes the materialized value.
    /// </summary>
    public override string ToString() =>
        $"ServiceSettingValue(Key={Key}, ValueType={ValueType}, HasValue={HasValue}, " +
        $"IsDefault={IsDefault}, IsSensitive={IsSensitive})";

    private T GetValue<T>(ServiceSettingValueType expectedType)
    {
        if (!HasValue)
        {
            throw new InvalidOperationException(
                $"The service setting '{Key}' does not have a value.");
        }

        if (ValueType != expectedType)
        {
            throw new InvalidOperationException(
                $"The service setting '{Key}' is not a {expectedType} value.");
        }

        return (T)value!;
    }
}

using System.Collections.Frozen;
using System.Text.Json;

namespace ServiceMantle.Configuration;

/// <summary>
/// Validates one already parsed setting value. Implementations must return only safe error codes.
/// </summary>
public interface IServiceSettingValueConstraint
{
    /// <summary>Gets the value type accepted by this constraint.</summary>
    ServiceSettingValueType ValueType { get; }

    /// <summary>Gets the safe error code returned when the constraint is not satisfied.</summary>
    string ErrorCode { get; }

    /// <summary>Determines whether the value satisfies the constraint.</summary>
    bool IsSatisfied(ServiceSettingValue value);
}

/// <summary>
/// Constrains the length of a string value.
/// </summary>
public sealed class StringLengthSettingConstraint : IServiceSettingValueConstraint
{
    /// <summary>Initializes a string length constraint.</summary>
    public StringLengthSettingConstraint(
        int minimumLength = 0,
        int maximumLength = int.MaxValue,
        string errorCode = "setting.string_length")
    {
        if (minimumLength < 0 || maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        }

        ServiceSettingValidationPrimitives.ValidateErrorCode(errorCode);
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the inclusive minimum length.</summary>
    public int MinimumLength { get; }

    /// <summary>Gets the inclusive maximum length.</summary>
    public int MaximumLength { get; }

    /// <inheritdoc />
    public ServiceSettingValueType ValueType => ServiceSettingValueType.String;

    /// <inheritdoc />
    public string ErrorCode { get; }

    /// <inheritdoc />
    public bool IsSatisfied(ServiceSettingValue value)
    {
        var length = value.GetString().Length;
        return length >= MinimumLength && length <= MaximumLength;
    }
}

/// <summary>
/// Constrains a decimal number to an inclusive range.
/// </summary>
public sealed class NumberRangeSettingConstraint : IServiceSettingValueConstraint
{
    /// <summary>Initializes a numeric range constraint.</summary>
    public NumberRangeSettingConstraint(
        decimal? minimum = null,
        decimal? maximum = null,
        string errorCode = "setting.number_range")
    {
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("The minimum cannot exceed the maximum.", nameof(minimum));
        }

        ServiceSettingValidationPrimitives.ValidateErrorCode(errorCode);
        Minimum = minimum;
        Maximum = maximum;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the inclusive minimum.</summary>
    public decimal? Minimum { get; }

    /// <summary>Gets the inclusive maximum.</summary>
    public decimal? Maximum { get; }

    /// <inheritdoc />
    public ServiceSettingValueType ValueType => ServiceSettingValueType.Number;

    /// <inheritdoc />
    public string ErrorCode { get; }

    /// <inheritdoc />
    public bool IsSatisfied(ServiceSettingValue value)
    {
        var number = value.GetNumber();
        return (Minimum is null || number >= Minimum) &&
            (Maximum is null || number <= Maximum);
    }
}

/// <summary>
/// Restricts a JSON value to one or more root kinds.
/// </summary>
public sealed class JsonRootKindSettingConstraint : IServiceSettingValueConstraint
{
    private readonly IReadOnlySet<JsonValueKind> allowedKinds;

    /// <summary>Initializes a JSON root-kind constraint.</summary>
    public JsonRootKindSettingConstraint(
        IEnumerable<JsonValueKind> allowedKinds,
        string errorCode = "setting.json_root_kind")
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);
        ServiceSettingValidationPrimitives.ValidateErrorCode(errorCode);

        var materialized = allowedKinds.ToHashSet();
        if (materialized.Count == 0 || materialized.Contains(JsonValueKind.Undefined))
        {
            throw new ArgumentException(
                "At least one defined JSON root kind is required.",
                nameof(allowedKinds));
        }

        this.allowedKinds = materialized.ToFrozenSet();
        ErrorCode = errorCode;
    }

    /// <inheritdoc />
    public ServiceSettingValueType ValueType => ServiceSettingValueType.Json;

    /// <inheritdoc />
    public string ErrorCode { get; }

    /// <summary>Gets the allowed JSON root kinds.</summary>
    public IReadOnlySet<JsonValueKind> AllowedKinds => allowedKinds;

    /// <inheritdoc />
    public bool IsSatisfied(ServiceSettingValue value) =>
        allowedKinds.Contains(value.GetJson().ValueKind);
}

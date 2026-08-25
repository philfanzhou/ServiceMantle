using System.Collections.ObjectModel;

namespace ServiceMantle.Configuration;

/// <summary>
/// Stable validation error codes emitted by the setting catalog.
/// </summary>
public static class WellKnownServiceSettingValidationErrorCodes
{
    /// <summary>A required value is missing or empty.</summary>
    public const string Required = "setting.required";

    /// <summary>A number cannot be parsed using invariant culture.</summary>
    public const string InvalidNumber = "setting.invalid_number";

    /// <summary>A Boolean value is not true or false.</summary>
    public const string InvalidBoolean = "setting.invalid_boolean";

    /// <summary>A JSON value is syntactically invalid.</summary>
    public const string InvalidJson = "setting.invalid_json";

    /// <summary>An input key is not registered in the catalog.</summary>
    public const string Unknown = "setting.unknown";

    /// <summary>Multiple input keys normalize to the same setting key.</summary>
    public const string Duplicate = "setting.duplicate";

    /// <summary>An input key has unsafe syntax.</summary>
    public const string InvalidKey = "setting.invalid_key";

    /// <summary>A value constraint threw or returned an unsafe result.</summary>
    public const string ConstraintFailed = "setting.constraint_failed";

    /// <summary>A composite validator failed unexpectedly.</summary>
    public const string CompositeValidationFailed = "setting.composite_validation_failed";
}

/// <summary>
/// Identifies one validation failure using only a normalized key and safe error code.
/// </summary>
public sealed record ServiceSettingValidationError
{
    /// <summary>Initializes a validation error.</summary>
    public ServiceSettingValidationError(string? key, string errorCode)
    {
        Key = key is null ? null : ServiceSettingValidationPrimitives.NormalizeKey(key);
        ServiceSettingValidationPrimitives.ValidateErrorCode(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>Gets the affected key, or null for a catalog-wide failure.</summary>
    public string? Key { get; }

    /// <summary>Gets the stable, non-secret error code.</summary>
    public string ErrorCode { get; }

    /// <summary>Returns only the key and safe error code.</summary>
    public override string ToString() =>
        $"ServiceSettingValidationError(Key={Key ?? "<catalog>"}, ErrorCode={ErrorCode})";
}

/// <summary>
/// Exposes a complete, successfully parsed candidate to product-owned combination validation.
/// </summary>
public sealed class ServiceSettingValidationContext
{
    private readonly IReadOnlyDictionary<string, ServiceSettingValue> values;

    internal ServiceSettingValidationContext(
        IReadOnlyDictionary<string, ServiceSettingValue> values)
    {
        this.values = values;
    }

    /// <summary>Gets all candidate values, including explicit missing optional values.</summary>
    public IReadOnlyDictionary<string, ServiceSettingValue> Values => values;

    /// <summary>Finds a candidate by normalized, case-insensitive key.</summary>
    public bool TryGetValue(string key, out ServiceSettingValue? value)
    {
        try
        {
            var normalizedKey = ServiceSettingValidationPrimitives.NormalizeKey(key);
            return values.TryGetValue(normalizedKey, out value);
        }
        catch
        {
            value = null;
            return false;
        }
    }
}

/// <summary>
/// Performs product-owned validation involving more than one setting.
/// </summary>
public interface IServiceSettingCompositeValidator
{
    /// <summary>
    /// Validates a complete candidate and returns only safe key/error-code failures.
    /// </summary>
    IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context);
}

/// <summary>
/// Contains either a complete validated value set or validation errors, never a partial value set.
/// </summary>
public sealed class ServiceSettingValidationResult
{
    private static readonly IReadOnlyDictionary<string, ServiceSettingValue> EmptyValues =
        new ReadOnlyDictionary<string, ServiceSettingValue>(
            new Dictionary<string, ServiceSettingValue>(StringComparer.OrdinalIgnoreCase));

    internal ServiceSettingValidationResult(
        IReadOnlyDictionary<string, ServiceSettingValue>? values,
        IReadOnlyList<ServiceSettingValidationError> errors)
    {
        Values = errors.Count == 0 ? values ?? EmptyValues : EmptyValues;
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Gets the complete value set on success, or an empty dictionary on any failure.
    /// </summary>
    public IReadOnlyDictionary<string, ServiceSettingValue> Values { get; }

    /// <summary>Gets safe validation failures.</summary>
    public IReadOnlyList<ServiceSettingValidationError> Errors { get; }

    /// <summary>Returns counts only and never includes setting values.</summary>
    public override string ToString() =>
        $"ServiceSettingValidationResult(IsValid={IsValid}, ValueCount={Values.Count}, ErrorCount={Errors.Count})";
}

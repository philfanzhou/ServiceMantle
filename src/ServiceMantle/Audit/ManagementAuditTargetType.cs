namespace ServiceMantle.Audit;

/// <summary>
/// Identifies the type of resource a management audit event affected, for example
/// <c>service</c> or a consumer-defined target type such as <c>signacore.account</c>.
/// </summary>
public sealed record ManagementAuditTargetType
{
    /// <summary>
    /// The maximum length allowed for a normalized target type value.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Gets the normalized target type value.
    /// </summary>
    public string Value { get; }

    private ManagementAuditTargetType(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Parses and normalizes a target type identifier.
    /// </summary>
    /// <exception cref="ManagementAuditException">The value is not a valid target type identifier.</exception>
    public static ManagementAuditTargetType Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var targetType) || targetType is null)
        {
            throw new ManagementAuditException(
                "audit.target_type_invalid",
                "The audit target type identifier has an invalid format.");
        }

        return targetType;
    }

    /// <summary>
    /// Attempts to parse and normalize a target type identifier.
    /// </summary>
    public static bool TryParse(string? value, out ManagementAuditTargetType? targetType)
    {
        if (value is null || !AuditCodeFormat.TryNormalize(value, MaxLength, out var normalizedValue))
        {
            targetType = null;
            return false;
        }

        targetType = new ManagementAuditTargetType(normalizedValue);
        return true;
    }

    /// <summary>
    /// Returns the normalized target type identifier.
    /// </summary>
    public override string ToString() => Value;
}

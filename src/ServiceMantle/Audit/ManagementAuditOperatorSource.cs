namespace ServiceMantle.Audit;

/// <summary>
/// Identifies where an audit event operator's identity assertion originated, for example
/// <c>system</c> or <c>interactive_admin</c>. See <see cref="WellKnownManagementAuditOperatorSources"/>
/// for common values; consuming services may define their own.
/// </summary>
public sealed record ManagementAuditOperatorSource
{
    /// <summary>
    /// The maximum length allowed for a normalized operator source value.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Gets the normalized operator source value.
    /// </summary>
    public string Value { get; }

    private ManagementAuditOperatorSource(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Parses and normalizes an operator source identifier.
    /// </summary>
    /// <exception cref="ManagementAuditException">The value is not a valid operator source identifier.</exception>
    public static ManagementAuditOperatorSource Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var source) || source is null)
        {
            throw new ManagementAuditException(
                "audit.operator_source_invalid",
                "The audit operator source identifier has an invalid format.");
        }

        return source;
    }

    /// <summary>
    /// Attempts to parse and normalize an operator source identifier.
    /// </summary>
    public static bool TryParse(string? value, out ManagementAuditOperatorSource? source)
    {
        if (value is null || !AuditCodeFormat.TryNormalize(value, MaxLength, out var normalizedValue))
        {
            source = null;
            return false;
        }

        source = new ManagementAuditOperatorSource(normalizedValue);
        return true;
    }

    /// <summary>
    /// Returns the normalized operator source identifier.
    /// </summary>
    public override string ToString() => Value;
}

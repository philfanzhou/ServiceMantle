namespace ServiceMantle.Audit;

/// <summary>
/// Identifies the action recorded by a management audit event, for example
/// <c>installation.completed</c> or a consumer-defined action such as <c>signacore.account_created</c>.
/// </summary>
public sealed record ManagementAuditAction
{
    /// <summary>
    /// The maximum length allowed for a normalized action value.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Gets the normalized action value.
    /// </summary>
    public string Value { get; }

    private ManagementAuditAction(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Parses and normalizes an action identifier.
    /// </summary>
    /// <exception cref="ManagementAuditException">The value is not a valid action identifier.</exception>
    public static ManagementAuditAction Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var action) || action is null)
        {
            throw new ManagementAuditException(
                "audit.action_invalid",
                "The audit action identifier has an invalid format.");
        }

        return action;
    }

    /// <summary>
    /// Attempts to parse and normalize an action identifier.
    /// </summary>
    public static bool TryParse(string? value, out ManagementAuditAction? action)
    {
        if (value is null || !AuditCodeFormat.TryNormalize(value, MaxLength, out var normalizedValue))
        {
            action = null;
            return false;
        }

        action = new ManagementAuditAction(normalizedValue);
        return true;
    }

    /// <summary>
    /// Returns the normalized action identifier.
    /// </summary>
    public override string ToString() => Value;
}

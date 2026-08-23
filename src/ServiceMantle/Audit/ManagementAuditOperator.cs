namespace ServiceMantle.Audit;

/// <summary>
/// Identifies who or what performed an audited management action: an optional operator identifier,
/// an optional display name, and the identity source the operator was authenticated through.
/// </summary>
public sealed record ManagementAuditOperator
{
    /// <summary>
    /// The maximum length allowed for an operator identifier.
    /// </summary>
    public const int MaxOperatorIdLength = 256;

    /// <summary>
    /// The maximum length allowed for an operator display name.
    /// </summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>
    /// Gets the operator identifier, or null when no operator identity is available (for example a
    /// system-initiated event).
    /// </summary>
    public string? OperatorId { get; }

    /// <summary>
    /// Gets the operator display name, or null when unavailable.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the identity source the operator was authenticated through.
    /// </summary>
    public ManagementAuditOperatorSource Source { get; }

    private ManagementAuditOperator(string? operatorId, string? displayName, ManagementAuditOperatorSource source)
    {
        OperatorId = operatorId;
        DisplayName = displayName;
        Source = source;
    }

    /// <summary>
    /// Creates a validated audit operator.
    /// </summary>
    /// <exception cref="ManagementAuditException">The operator identifier or display name is invalid.</exception>
    public static ManagementAuditOperator Create(
        ManagementAuditOperatorSource source,
        string? operatorId = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var cleanedOperatorId = AuditTextSanitizer.Clean(
            operatorId,
            MaxOperatorIdLength,
            "audit.operator_id_invalid",
            "operator identifier");
        var cleanedDisplayName = AuditTextSanitizer.Clean(
            displayName,
            MaxDisplayNameLength,
            "audit.operator_display_name_invalid",
            "operator display name");

        if (cleanedOperatorId is not null)
        {
            ManagementAuditContentSanitizer.EnsureNoSensitiveContent(
                cleanedOperatorId,
                "audit.operator_id_invalid",
                "operator identifier");
        }

        if (cleanedDisplayName is not null)
        {
            cleanedDisplayName = ManagementAuditContentSanitizer.Redact(cleanedDisplayName);
        }

        return new ManagementAuditOperator(cleanedOperatorId, cleanedDisplayName, source);
    }

    /// <summary>
    /// Creates a system-initiated operator with no human identity, for example during installation.
    /// </summary>
    public static ManagementAuditOperator System(string? displayName = null) =>
        Create(WellKnownManagementAuditOperatorSources.System, operatorId: null, displayName: displayName);

    /// <summary>
    /// Returns a safe projection that never includes the display name.
    /// </summary>
    public override string ToString() => $"ManagementAuditOperator(Source={Source}, OperatorId={OperatorId})";
}

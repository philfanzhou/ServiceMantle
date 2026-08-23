namespace ServiceMantle.Audit;

/// <summary>
/// Identifies the resource a management audit event affected: its type and an opaque identifier
/// owned by the consuming service (for example a primary key, natural key, or composite string).
/// </summary>
public sealed record ManagementAuditTarget
{
    /// <summary>
    /// The maximum length allowed for a target identifier.
    /// </summary>
    public const int MaxTargetIdLength = 256;

    /// <summary>
    /// Gets the target's type.
    /// </summary>
    public ManagementAuditTargetType Type { get; }

    /// <summary>
    /// Gets the target's identifier within its type.
    /// </summary>
    public string Id { get; }

    private ManagementAuditTarget(ManagementAuditTargetType type, string id)
    {
        Type = type;
        Id = id;
    }

    /// <summary>
    /// Creates a validated audit target.
    /// </summary>
    /// <exception cref="ManagementAuditException">The target identifier is invalid.</exception>
    public static ManagementAuditTarget Create(ManagementAuditTargetType type, string id)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(id);

        var cleanedId = AuditTextSanitizer.CleanRequired(
            id,
            MaxTargetIdLength,
            "audit.target_id_invalid",
            "target identifier");

        ManagementAuditContentSanitizer.EnsureNoSensitiveContent(
            cleanedId,
            "audit.target_id_invalid",
            "target identifier");

        return new ManagementAuditTarget(type, cleanedId);
    }

    /// <summary>
    /// Returns a safe projection combining type and identifier.
    /// </summary>
    public override string ToString() => $"{Type}:{Id}";
}

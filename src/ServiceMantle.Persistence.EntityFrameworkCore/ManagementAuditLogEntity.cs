using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Entity storing a management audit record in a shared business database.
/// </summary>
public sealed class ManagementAuditLogEntity
{
    /// <summary>
    /// Gets or sets the unique identifier of the audit record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the operator identifier, or null when no operator identity is available.
    /// </summary>
    public string? OperatorId { get; set; }

    /// <summary>
    /// Gets or sets the operator display name, or null when unavailable.
    /// </summary>
    public string? OperatorDisplayName { get; set; }

    /// <summary>
    /// Gets or sets the normalized operator identity source.
    /// </summary>
    public string OperatorSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized action identifier.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized target type identifier.
    /// </summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target identifier.
    /// </summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the security-relevant outcome.
    /// </summary>
    public ManagementAuditOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the action occurred.
    /// </summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the client IP the action was attributed to, or null when unavailable.
    /// </summary>
    public string? ClientIp { get; set; }

    /// <summary>
    /// Gets or sets the correlation identifier linking this event to a broader operation, or null.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the sanitized security description, or null.
    /// </summary>
    public string? SecurityDescription { get; set; }

    /// <summary>
    /// Gets or sets the sanitized structured metadata, serialized as JSON, or null when empty.
    /// </summary>
    public string? MetadataJson { get; set; }

    /// <summary>
    /// Returns a safe projection for debugging.
    /// </summary>
    public override string ToString() =>
        $"ManagementAuditLogEntity(Id={Id}, Action={Action}, TargetType={TargetType}, TargetId={TargetId})";
}

using System.Collections.Frozen;

namespace ServiceMantle.Audit;

/// <summary>
/// A persisted management audit record, as staged by a write or returned by a query.
/// </summary>
public sealed record ManagementAuditRecord
{
    /// <summary>
    /// Gets the unique identifier assigned to this audit record.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets who or what performed the action.
    /// </summary>
    public ManagementAuditOperator Operator { get; }

    /// <summary>
    /// Gets the action performed.
    /// </summary>
    public ManagementAuditAction Action { get; }

    /// <summary>
    /// Gets the resource the action affected.
    /// </summary>
    public ManagementAuditTarget Target { get; }

    /// <summary>
    /// Gets the security-relevant outcome of the action.
    /// </summary>
    public ManagementAuditOutcome Outcome { get; }

    /// <summary>
    /// Gets the UTC time the action occurred.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>
    /// Gets the client IP the action was attributed to, or null when unavailable.
    /// </summary>
    public string? ClientIp { get; }

    /// <summary>
    /// Gets the correlation identifier linking this event to a broader operation, or null.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Gets the sanitized security description, or null.
    /// </summary>
    public string? SecurityDescription { get; }

    /// <summary>
    /// Gets sanitized structured metadata for the event.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public ManagementAuditRecord(
        Guid id,
        ManagementAuditOperator operatorInfo,
        ManagementAuditAction action,
        ManagementAuditTarget target,
        ManagementAuditOutcome outcome,
        DateTimeOffset occurredAtUtc,
        string? clientIp,
        string? correlationId,
        string? securityDescription,
        IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(operatorInfo);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(metadata);

        Id = id;
        Operator = operatorInfo;
        Action = action;
        Target = target;
        Outcome = outcome;
        OccurredAtUtc = occurredAtUtc;
        ClientIp = clientIp;
        CorrelationId = correlationId;
        SecurityDescription = securityDescription;
        Metadata = metadata.Count == 0
            ? FrozenDictionary<string, string>.Empty
            : metadata.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns a safe projection that never includes the description or metadata content.
    /// </summary>
    public override string ToString() =>
        $"ManagementAuditRecord(Id={Id}, Action={Action}, Target={Target}, Outcome={Outcome})";
}

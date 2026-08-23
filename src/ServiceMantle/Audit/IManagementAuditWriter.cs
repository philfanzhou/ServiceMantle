namespace ServiceMantle.Audit;

/// <summary>
/// Stages a management audit record for persistence. Implementations must participate in the
/// caller's existing unit of work and must not commit or otherwise finalize a transaction the caller
/// owns; the caller decides when its business transaction commits.
/// </summary>
public interface IManagementAuditWriter
{
    /// <summary>
    /// Stages the audit event for persistence as part of the caller's current unit of work.
    /// </summary>
    /// <param name="auditEvent">The validated, sanitized audit event to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The staged audit record, including its assigned identifier.</returns>
    ValueTask<ManagementAuditRecord> RecordAsync(
        ManagementAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}

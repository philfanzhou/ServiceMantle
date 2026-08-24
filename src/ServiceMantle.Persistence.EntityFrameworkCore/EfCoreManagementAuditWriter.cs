using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core-based <see cref="IManagementAuditWriter"/> that stages audit records on the caller's
/// business DbContext without calling <c>SaveChangesAsync</c>. The caller decides when to save and
/// commit, so the audit write always participates in whatever unit of work or transaction the caller
/// already owns and never finalizes it on the caller's behalf.
/// </summary>
public sealed class EfCoreManagementAuditWriter<TDbContext> : IManagementAuditWriter
    where TDbContext : DbContext
{
    private readonly TDbContext dbContext;

    /// <summary>
    /// Initializes a new management audit writer.
    /// </summary>
    /// <param name="dbContext">
    /// The business DbContext whose model includes <see cref="ManagementAuditModelBuilderExtensions.AddServiceMantleManagementAudit"/>.
    /// </param>
    public EfCoreManagementAuditWriter(TDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        this.dbContext = dbContext;
    }

    /// <summary>
    /// Stages the audit event by adding it to the caller's DbContext change tracker. This method does
    /// not call <c>SaveChangesAsync</c>; the caller must save changes (and commit any explicit
    /// transaction) as part of its own unit of work for the record to be persisted.
    /// </summary>
    public ValueTask<ManagementAuditRecord> RecordAsync(
        ManagementAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Guid.NewGuid();
        var entity = ManagementAuditEntityMapper.ConvertToEntity(id, auditEvent);
        var record = ManagementAuditEntityMapper.ConvertToRecord(entity);
        dbContext.Set<ManagementAuditLogEntity>().Add(entity);

        return ValueTask.FromResult(record);
    }
}

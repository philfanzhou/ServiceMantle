using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Contract that a business DbContext implements for ServiceMantle management audit persistence.
/// Deliberately separate from <see cref="IServiceMantleDbContext"/> so consuming DbContexts can adopt
/// installation persistence, audit persistence, or both independently.
/// </summary>
public interface IServiceMantleAuditDbContext
{
    /// <summary>
    /// Gets management audit log entities.
    /// </summary>
    DbSet<ManagementAuditLogEntity> ServiceAuditLogs { get; }
}

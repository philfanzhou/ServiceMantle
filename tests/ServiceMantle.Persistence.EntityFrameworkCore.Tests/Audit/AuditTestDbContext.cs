using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

/// <summary>
/// A minimal business DbContext stand-in that adopts management audit persistence alongside a
/// hypothetical business entity, used to verify the audit writer participates in the caller's
/// existing unit of work and transaction.
/// </summary>
internal sealed class AuditTestDbContext : DbContext, IServiceMantleAuditDbContext
{
    public AuditTestDbContext(DbContextOptions<AuditTestDbContext> options)
        : base(options)
    {
    }

    public DbSet<ManagementAuditLogEntity> ServiceAuditLogs { get; set; } = null!;

    public DbSet<BusinessWidgetEntity> Widgets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);

        modelBuilder.Entity<BusinessWidgetEntity>(entity =>
        {
            entity.ToTable("business_widgets");
            entity.HasKey(item => item.Id);
        });
    }
}

/// <summary>
/// A stand-in for an unrelated business entity owned by the consuming service, used only to prove
/// audit writes participate in the same unit of work as other business writes.
/// </summary>
internal sealed class BusinessWidgetEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

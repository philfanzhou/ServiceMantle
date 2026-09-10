using Microsoft.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Data;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// The consumer-owned PostgreSQL context for the sample's workspace tables.
/// </summary>
/// <remarks>
/// <para>
/// This context is deliberately separate from the sample's SQLite <see cref="ReferenceDbContext"/>.
/// The SQLite context and its migration are written against SQLite's own storage types, so the same
/// migration cannot be replayed on PostgreSQL by swapping the provider. This context owns its own
/// migration history and maps the shared <see cref="ReferenceWorkspace"/> entity to PostgreSQL
/// storage types instead.
/// </para>
/// <para>
/// It maps only the workspace business table. Installation state, setup, configuration, and audit
/// tables are not part of this context, and the schema it describes is never evidence that a service
/// installation has been completed.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlDbContext(DbContextOptions<ReferencePostgreSqlDbContext> options)
    : DbContext(options)
{
    /// <summary>The workspace table this sample owns.</summary>
    public DbSet<ReferenceWorkspace> Workspaces => Set<ReferenceWorkspace>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReferenceWorkspace>(entity =>
        {
            entity.ToTable("reference_workspaces");
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Id).ValueGeneratedNever().HasColumnType("uuid");
            entity.Property(workspace => workspace.DisplayName)
                .IsRequired()
                .HasMaxLength(120)
                .HasColumnType("character varying(120)");
        });
    }
}

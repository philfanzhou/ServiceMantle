using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Data;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// The consumer-owned PostgreSQL context for the sample's workspace and installation tables.
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
/// Besides the workspace business table it maps the ServiceMantle installation table through the
/// public EF Core persistence package's own mapping, so the storage types, lengths, defaults, and
/// the version concurrency token stay owned by that package. Mapping the table is schema only: the
/// schema it describes is never evidence that a service installation has been completed, and this
/// context creates no installation rows on its own.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlDbContext(DbContextOptions<ReferencePostgreSqlDbContext> options)
    : DbContext(options), IServiceDbContext
{
    /// <summary>The workspace table this sample owns.</summary>
    public DbSet<ReferenceWorkspace> Workspaces => Set<ReferenceWorkspace>();

    /// <summary>The ServiceMantle installation table mapped by the persistence package.</summary>
    public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

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
        modelBuilder.AddServiceMantleInstallation();
    }
}

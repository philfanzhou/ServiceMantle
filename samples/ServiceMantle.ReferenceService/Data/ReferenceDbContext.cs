using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.ReferenceService.Data;

/// <summary>The consumer owns this context, its schema and every save/transaction boundary.</summary>
public sealed class ReferenceDbContext(DbContextOptions<ReferenceDbContext> options) : DbContext(options)
{
    public DbSet<ReferenceWorkspace> Workspaces => Set<ReferenceWorkspace>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReferenceWorkspace>(entity =>
        {
            entity.ToTable("reference_workspaces");
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Id).ValueGeneratedNever();
            entity.Property(workspace => workspace.DisplayName).IsRequired().HasMaxLength(120);
        });
    }
}

public sealed class ReferenceWorkspace
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

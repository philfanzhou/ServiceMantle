using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ServiceMantle.ReferenceService.Database.PostgreSql.Migrations;

[DbContext(typeof(ReferencePostgreSqlDbContext))]
[Migration("20260910000000_InitialReferencePostgreSqlWorkspace")]
public sealed class InitialReferencePostgreSqlWorkspace : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "reference_workspaces",
        columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            DisplayName = table.Column<string>(
                type: "character varying(120)",
                maxLength: 120,
                nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_reference_workspaces", row => row.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("reference_workspaces");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        InitialReferencePostgreSqlModel.Build(modelBuilder);
}

// Frozen initial model shared only by this migration and its initial snapshot.
internal static class InitialReferencePostgreSqlModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        modelBuilder.Entity("ServiceMantle.ReferenceService.Data.ReferenceWorkspace", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            entity.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(120)
                .HasColumnType("character varying(120)");
            entity.HasKey("Id");
            entity.ToTable("reference_workspaces");
        });
    }
}

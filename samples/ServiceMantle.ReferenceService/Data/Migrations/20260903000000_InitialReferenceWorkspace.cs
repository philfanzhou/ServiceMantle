using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ServiceMantle.ReferenceService.Data.Migrations;

[DbContext(typeof(ReferenceDbContext))]
[Migration("20260903000000_InitialReferenceWorkspace")]
public sealed class InitialReferenceWorkspace : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "reference_workspaces",
        columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_reference_workspaces", row => row.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("reference_workspaces");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InitialReferenceModel.Build(modelBuilder);
}

// Frozen initial model shared only by this migration and its initial snapshot.
internal static class InitialReferenceModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        modelBuilder.Entity("ServiceMantle.ReferenceService.Data.ReferenceWorkspace", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("TEXT");
            entity.Property<string>("DisplayName").IsRequired().HasMaxLength(120).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("reference_workspaces");
        });
    }
}

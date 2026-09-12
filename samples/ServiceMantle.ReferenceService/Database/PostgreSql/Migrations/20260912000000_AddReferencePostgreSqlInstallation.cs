using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ServiceMantle.ReferenceService.Database.PostgreSql.Migrations;

[DbContext(typeof(ReferencePostgreSqlDbContext))]
[Migration("20260912000000_AddReferencePostgreSqlInstallation")]
public sealed class AddReferencePostgreSqlInstallation : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "service_installations",
        columns: table => new
        {
            service_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
            status = table.Column<int>(type: "integer", nullable: false),
            created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            version = table.Column<int>(type: "integer", nullable: false),
            setup_code_generation = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
            setup_code_digest = table.Column<string>(type: "character varying(74)", maxLength: 74, nullable: true),
            setup_code_issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            setup_code_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
        },
        constraints: table => table.PrimaryKey("PK_service_installations", row => row.service_id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("service_installations");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        CurrentReferencePostgreSqlModel.Build(modelBuilder);
}

// Frozen current model shared by this migration and the latest snapshot. It is the initial frozen
// model plus the ServiceMantle installation table, whose storage facets stay owned by the public
// EF Core persistence package's own mapping.
internal static class CurrentReferencePostgreSqlModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        InitialReferencePostgreSqlModel.Build(modelBuilder);
        modelBuilder.Entity("ServiceMantle.Persistence.EntityFrameworkCore.ServiceInstallationEntity", entity =>
        {
            entity.Property<string>("ServiceId")
                .HasColumnName("service_id")
                .HasColumnType("character varying(128)")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property<global::ServiceMantle.Installation.InstallationStatus>("Status")
                .HasColumnName("status")
                .HasColumnType("integer")
                .HasConversion<int>()
                .IsRequired();
            entity.Property<DateTime>("CreatedAtUtc")
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property<DateTime?>("CompletedAtUtc")
                .HasColumnName("completed_at_utc")
                .HasColumnType("timestamp with time zone");
            entity.Property<int>("Version")
                .HasColumnName("version")
                .HasColumnType("integer")
                .IsRequired()
                .IsConcurrencyToken();
            entity.Property<int>("SetupCodeGeneration")
                .HasColumnName("setup_code_generation")
                .HasColumnType("integer")
                .IsRequired()
                .HasDefaultValue(0);
            entity.Property<string?>("SetupCodeDigest")
                .HasColumnName("setup_code_digest")
                .HasColumnType("character varying(74)")
                .HasMaxLength(74);
            entity.Property<DateTime?>("SetupCodeIssuedAtUtc")
                .HasColumnName("setup_code_issued_at_utc")
                .HasColumnType("timestamp with time zone");
            entity.Property<DateTime?>("SetupCodeExpiresAtUtc")
                .HasColumnName("setup_code_expires_at_utc")
                .HasColumnType("timestamp with time zone");
            entity.HasKey("ServiceId");
            entity.ToTable("service_installations");
        });
    }
}

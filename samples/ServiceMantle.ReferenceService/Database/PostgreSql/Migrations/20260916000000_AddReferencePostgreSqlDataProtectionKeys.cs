using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ServiceMantle.ReferenceService.Database.PostgreSql.Migrations;

[DbContext(typeof(ReferencePostgreSqlDbContext))]
[Migration("20260916000000_AddReferencePostgreSqlDataProtectionKeys")]
public sealed class AddReferencePostgreSqlDataProtectionKeys : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "service_data_protection_keys",
        columns: table => new
        {
            service_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
            key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            encrypted_xml = table.Column<string>(type: "text", nullable: false)
        },
        constraints: table => table.PrimaryKey(
            "PK_service_data_protection_keys",
            row => new { row.service_id, row.key_id }));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("service_data_protection_keys");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        CurrentReferencePostgreSqlModelWithProtectionKeys.Build(modelBuilder);
}

// Frozen current model shared by this migration and the latest snapshot. It is the previous frozen
// model plus the ServiceMantle data-protection key table, whose storage facets stay owned by the
// public EF Core persistence package's own mapping.
internal static class CurrentReferencePostgreSqlModelWithProtectionKeys
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        CurrentReferencePostgreSqlModel.Build(modelBuilder);
        modelBuilder.Entity("ServiceMantle.Persistence.EntityFrameworkCore.DataProtectionKeyEntity", entity =>
        {
            entity.Property<string>("ServiceId")
                .HasColumnName("service_id")
                .HasColumnType("character varying(128)")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property<string>("KeyId")
                .HasColumnName("key_id")
                .HasColumnType("character varying(64)")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property<string>("EncryptedXml")
                .HasColumnName("encrypted_xml")
                .HasColumnType("text")
                .IsRequired();
            entity.HasKey("ServiceId", "KeyId");
            entity.ToTable("service_data_protection_keys");
        });
    }
}

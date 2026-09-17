using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ServiceMantle.ReferenceService.Database.PostgreSql.Migrations;

[DbContext(typeof(ReferencePostgreSqlDbContext))]
[Migration("20260917000000_AddReferencePostgreSqlSettingsAndAudit")]
public sealed class AddReferencePostgreSqlSettingsAndAudit : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "service_settings",
            columns: table => new
            {
                service_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                values_json = table.Column<string>(type: "text", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                restart_required = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_service_settings", x => x.service_id);
                table.CheckConstraint("ck_service_settings_version", "version > 0");
            });

        migrationBuilder.CreateTable(
            name: "service_audit_logs",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                operator_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                operator_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                operator_source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                target_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                outcome = table.Column<int>(type: "integer", nullable: false),
                occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                client_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                security_description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                metadata_json = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_service_audit_logs", x => x.id);
                table.CheckConstraint("ck_service_audit_logs_action_length", "action IS NULL OR octet_length(action) <= 800");
                table.CheckConstraint("ck_service_audit_logs_client_ip_length", "client_ip IS NULL OR octet_length(client_ip) <= 256");
                table.CheckConstraint("ck_service_audit_logs_correlation_id_length", "correlation_id IS NULL OR octet_length(correlation_id) <= 512");
                table.CheckConstraint("ck_service_audit_logs_id_length", "id IS NULL OR octet_length(id) <= 144");
                table.CheckConstraint("ck_service_audit_logs_id_not_empty", "id <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("ck_service_audit_logs_metadata_json_length", "metadata_json IS NULL OR octet_length(metadata_json) <= 262144");
                table.CheckConstraint("ck_service_audit_logs_operator_display_name_length", "operator_display_name IS NULL OR octet_length(operator_display_name) <= 1024");
                table.CheckConstraint("ck_service_audit_logs_operator_id_length", "operator_id IS NULL OR octet_length(operator_id) <= 1024");
                table.CheckConstraint("ck_service_audit_logs_operator_source_length", "operator_source IS NULL OR octet_length(operator_source) <= 400");
                table.CheckConstraint("ck_service_audit_logs_security_description_length", "security_description IS NULL OR octet_length(security_description) <= 16000");
                table.CheckConstraint("ck_service_audit_logs_target_id_length", "target_id IS NULL OR octet_length(target_id) <= 1024");
                table.CheckConstraint("ck_service_audit_logs_target_type_length", "target_type IS NULL OR octet_length(target_type) <= 800");
            });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "occurred_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_action_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "action", "occurred_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_operator_id_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "operator_id", "occurred_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_target_id_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "target_id", "occurred_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_target_type_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "target_type", "occurred_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_service_audit_logs_target_occurred_at_utc_id",
            table: "service_audit_logs",
            columns: new[] { "target_type", "target_id", "occurred_at_utc", "id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dependency order: the audit table references nothing, but it is dropped first so the
        // removal mirrors the arrival order in reverse and stays a development-only rollback.
        migrationBuilder.DropTable("service_audit_logs");
        migrationBuilder.DropTable("service_settings");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        CurrentReferencePostgreSqlModelWithSettingsAndAudit.Build(modelBuilder);
}

// Frozen current model shared by this migration and the latest snapshot. It is the previous frozen
// model plus the ServiceMantle settings and management audit tables, whose storage facets stay
// owned by the public EF Core persistence package's own mappings.
internal static class CurrentReferencePostgreSqlModelWithSettingsAndAudit
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        CurrentReferencePostgreSqlModelWithProtectionKeys.Build(modelBuilder);
        modelBuilder.Entity("ServiceMantle.Persistence.EntityFrameworkCore.ServiceSettingEntity", entity =>
        {
            entity.ToTable("service_settings", table =>
                table.HasCheckConstraint("ck_service_settings_version", "version > 0"));
            entity.Property<string>("ServiceId")
                .HasColumnName("service_id")
                .HasColumnType("character varying(128)")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property<string>("ValuesJson")
                .HasColumnName("values_json")
                .HasColumnType("text")
                .IsRequired();
            entity.Property<long>("Version")
                .HasColumnName("version")
                .HasColumnType("bigint")
                .IsConcurrencyToken()
                .IsRequired();
            entity.Property<DateTime>("UpdatedAtUtc")
                .HasColumnName("updated_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property<string>("UpdatedBy")
                .HasColumnName("updated_by")
                .HasColumnType("character varying(256)")
                .HasMaxLength(256)
                .IsRequired();
            entity.Property<bool>("RestartRequired")
                .HasColumnName("restart_required")
                .HasColumnType("boolean")
                .IsRequired();
            entity.HasKey("ServiceId");
        });
        modelBuilder.Entity("ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditLogEntity", entity =>
        {
            entity.ToTable(
                "service_audit_logs",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_service_audit_logs_id_not_empty",
                        "id <> '00000000-0000-0000-0000-000000000000'");
                    table.HasCheckConstraint("ck_service_audit_logs_id_length", "id IS NULL OR octet_length(id) <= 144");
                    table.HasCheckConstraint("ck_service_audit_logs_operator_id_length", "operator_id IS NULL OR octet_length(operator_id) <= 1024");
                    table.HasCheckConstraint("ck_service_audit_logs_operator_display_name_length", "operator_display_name IS NULL OR octet_length(operator_display_name) <= 1024");
                    table.HasCheckConstraint("ck_service_audit_logs_operator_source_length", "operator_source IS NULL OR octet_length(operator_source) <= 400");
                    table.HasCheckConstraint("ck_service_audit_logs_action_length", "action IS NULL OR octet_length(action) <= 800");
                    table.HasCheckConstraint("ck_service_audit_logs_target_type_length", "target_type IS NULL OR octet_length(target_type) <= 800");
                    table.HasCheckConstraint("ck_service_audit_logs_target_id_length", "target_id IS NULL OR octet_length(target_id) <= 1024");
                    table.HasCheckConstraint("ck_service_audit_logs_client_ip_length", "client_ip IS NULL OR octet_length(client_ip) <= 256");
                    table.HasCheckConstraint("ck_service_audit_logs_correlation_id_length", "correlation_id IS NULL OR octet_length(correlation_id) <= 512");
                    table.HasCheckConstraint("ck_service_audit_logs_security_description_length", "security_description IS NULL OR octet_length(security_description) <= 16000");
                    table.HasCheckConstraint("ck_service_audit_logs_metadata_json_length", "metadata_json IS NULL OR octet_length(metadata_json) <= 262144");
                });
            entity.Property<string>("Id")
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .HasMaxLength(36)
                .IsRequired();
            entity.Property<string>("OperatorId")
                .HasColumnName("operator_id")
                .HasColumnType("character varying(256)")
                .HasMaxLength(256);
            entity.Property<string>("OperatorDisplayName")
                .HasColumnName("operator_display_name")
                .HasColumnType("character varying(256)")
                .HasMaxLength(256);
            entity.Property<string>("OperatorSource")
                .HasColumnName("operator_source")
                .HasColumnType("character varying(100)")
                .HasMaxLength(100)
                .IsRequired();
            entity.Property<string>("Action")
                .HasColumnName("action")
                .HasColumnType("character varying(200)")
                .HasMaxLength(200)
                .IsRequired();
            entity.Property<string>("TargetType")
                .HasColumnName("target_type")
                .HasColumnType("character varying(200)")
                .HasMaxLength(200)
                .IsRequired();
            entity.Property<string>("TargetId")
                .HasColumnName("target_id")
                .HasColumnType("character varying(256)")
                .HasMaxLength(256)
                .IsRequired();
            entity.Property<int>("Outcome")
                .HasColumnName("outcome")
                .HasColumnType("integer")
                .IsRequired();
            entity.Property<DateTime>("OccurredAtUtc")
                .HasColumnName("occurred_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property<string>("ClientIp")
                .HasColumnName("client_ip")
                .HasColumnType("character varying(64)")
                .HasMaxLength(64);
            entity.Property<string>("CorrelationId")
                .HasColumnName("correlation_id")
                .HasColumnType("character varying(128)")
                .HasMaxLength(128);
            entity.Property<string>("SecurityDescription")
                .HasColumnName("security_description")
                .HasColumnType("character varying(4000)")
                .HasMaxLength(4000);
            entity.Property<string>("MetadataJson")
                .HasColumnName("metadata_json")
                .HasColumnType("character varying(262144)")
                .HasMaxLength(262144);
            entity.HasKey("Id");
            entity.HasIndex("OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_occurred_at_utc_id");
            entity.HasIndex("Action", "OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_action_occurred_at_utc_id");
            entity.HasIndex("TargetType", "TargetId", "OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_target_occurred_at_utc_id");
            entity.HasIndex("TargetType", "OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_target_type_occurred_at_utc_id");
            entity.HasIndex("TargetId", "OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_target_id_occurred_at_utc_id");
            entity.HasIndex("OperatorId", "OccurredAtUtc", "Id")
                .HasDatabaseName("ix_service_audit_logs_operator_id_occurred_at_utc_id");
        });
    }
}

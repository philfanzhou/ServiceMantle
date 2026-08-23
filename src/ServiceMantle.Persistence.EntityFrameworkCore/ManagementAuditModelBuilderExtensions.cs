using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Adds ServiceMantle management audit entity configuration to application models.
/// </summary>
public static class ManagementAuditModelBuilderExtensions
{
    /// <summary>
    /// Adds the ServiceMantle management audit log entity to the model.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="databaseDialect">The SQL dialect used by the consuming DbContext.</param>
    /// <returns>The same model builder for fluent use.</returns>
    public static ModelBuilder AddServiceMantleManagementAudit(
        this ModelBuilder modelBuilder,
        ManagementAuditDatabaseDialect databaseDialect)
    {
        if (modelBuilder is null)
        {
            throw new ArgumentNullException(nameof(modelBuilder));
        }

        var byteLengthMethod = typeof(ManagementAuditDatabaseFunctions).GetMethod(
            nameof(ManagementAuditDatabaseFunctions.MetadataJsonByteLength),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        modelBuilder.HasDbFunction(byteLengthMethod)
            .HasName(GetMetadataJsonByteLengthFunction(databaseDialect))
            .IsBuiltIn()
            .HasParameter("value")
            .PropagatesNullability();

        modelBuilder.Entity<ManagementAuditLogEntity>(entity =>
        {
            entity.ToTable(
                "service_audit_logs",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_service_audit_logs_id_not_empty",
                        "id <> '00000000-0000-0000-0000-000000000000'");
                    table.HasCheckConstraint(
                        "ck_service_audit_logs_metadata_json_length",
                        GetMetadataJsonLengthConstraint(databaseDialect));
                });

            entity.HasKey(item => item.Id);

            entity.Property(item => item.Id)
                .HasColumnName("id")
                .HasConversion(
                    value => value.ToString("D"),
                    value => Guid.Parse(value))
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(item => item.OperatorId)
                .HasColumnName("operator_id")
                .HasMaxLength(256);

            entity.Property(item => item.OperatorDisplayName)
                .HasColumnName("operator_display_name")
                .HasMaxLength(256);

            entity.Property(item => item.OperatorSource)
                .HasColumnName("operator_source")
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(item => item.Action)
                .HasColumnName("action")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(item => item.TargetType)
                .HasColumnName("target_type")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(item => item.TargetId)
                .HasColumnName("target_id")
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(item => item.Outcome)
                .HasColumnName("outcome")
                .HasConversion<int>()
                .IsRequired();

            entity.Property(item => item.OccurredAtUtc)
                .HasColumnName("occurred_at_utc")
                .IsRequired();

            entity.Property(item => item.ClientIp)
                .HasColumnName("client_ip")
                .HasMaxLength(64);

            entity.Property(item => item.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(128);

            entity.Property(item => item.SecurityDescription)
                .HasColumnName("security_description")
                .HasMaxLength(4000);

            entity.Property(item => item.MetadataJson)
                .HasColumnName("metadata_json")
                .HasMaxLength(ManagementAuditEntityMapper.MaxMetadataJsonByteLength);

            entity.HasIndex(item => new { item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_occurred_at_utc_id");

            entity.HasIndex(item => new { item.Action, item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_action_occurred_at_utc_id");

            entity.HasIndex(item => new { item.TargetType, item.TargetId, item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_target_occurred_at_utc_id");

            entity.HasIndex(item => new { item.TargetType, item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_target_type_occurred_at_utc_id");

            entity.HasIndex(item => new { item.TargetId, item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_target_id_occurred_at_utc_id");

            entity.HasIndex(item => new { item.OperatorId, item.OccurredAtUtc, item.Id })
                .HasDatabaseName("ix_service_audit_logs_operator_id_occurred_at_utc_id");
        });

        return modelBuilder;
    }

    private static string GetMetadataJsonLengthConstraint(ManagementAuditDatabaseDialect databaseDialect) =>
        databaseDialect switch
        {
            ManagementAuditDatabaseDialect.Sqlite =>
                $"metadata_json IS NULL OR octet_length(metadata_json) <= {ManagementAuditEntityMapper.MaxMetadataJsonByteLength}",
            ManagementAuditDatabaseDialect.PostgreSql =>
                $"metadata_json IS NULL OR octet_length(metadata_json) <= {ManagementAuditEntityMapper.MaxMetadataJsonByteLength}",
            ManagementAuditDatabaseDialect.SqlServer =>
                $"metadata_json IS NULL OR DATALENGTH(metadata_json) <= {ManagementAuditEntityMapper.MaxMetadataJsonByteLength}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseDialect), databaseDialect, "The management audit database dialect is not supported.")
        };

    private static string GetMetadataJsonByteLengthFunction(ManagementAuditDatabaseDialect databaseDialect) =>
        databaseDialect switch
        {
            ManagementAuditDatabaseDialect.Sqlite => "octet_length",
            ManagementAuditDatabaseDialect.PostgreSql => "octet_length",
            ManagementAuditDatabaseDialect.SqlServer => "DATALENGTH",
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseDialect), databaseDialect, "The management audit database dialect is not supported.")
        };
}

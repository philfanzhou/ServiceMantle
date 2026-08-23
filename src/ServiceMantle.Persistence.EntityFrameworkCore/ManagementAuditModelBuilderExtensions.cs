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
    /// <returns>The same model builder for fluent use.</returns>
    public static ModelBuilder AddServiceMantleManagementAudit(this ModelBuilder modelBuilder)
    {
        if (modelBuilder is null)
        {
            throw new ArgumentNullException(nameof(modelBuilder));
        }

        modelBuilder.Entity<ManagementAuditLogEntity>(entity =>
        {
            entity.ToTable("service_audit_logs");

            entity.HasKey(item => item.Id);

            entity.Property(item => item.Id)
                .HasColumnName("id")
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
                .HasColumnName("metadata_json");

            entity.HasIndex(item => item.OccurredAtUtc)
                .HasDatabaseName("ix_service_audit_logs_occurred_at_utc");

            entity.HasIndex(item => item.Action)
                .HasDatabaseName("ix_service_audit_logs_action");

            entity.HasIndex(item => new { item.TargetType, item.TargetId })
                .HasDatabaseName("ix_service_audit_logs_target");

            entity.HasIndex(item => item.OperatorId)
                .HasDatabaseName("ix_service_audit_logs_operator_id");
        });

        return modelBuilder;
    }
}

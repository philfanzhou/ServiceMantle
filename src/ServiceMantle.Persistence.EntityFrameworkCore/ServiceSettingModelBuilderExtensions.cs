using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>Adds shared service setting persistence to consuming EF Core models.</summary>
public static class ServiceSettingModelBuilderExtensions
{
    /// <summary>Adds the provider-agnostic <c>service_settings</c> aggregate mapping.</summary>
    public static ModelBuilder AddServiceMantleSettings(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ServiceSettingEntity>(entity =>
        {
            entity.ToTable("service_settings", table =>
                table.HasCheckConstraint("ck_service_settings_version", "version > 0"));
            entity.HasKey(item => item.ServiceId);
            entity.Property(item => item.ServiceId)
                .HasColumnName("service_id")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(item => item.ValuesJson)
                .HasColumnName("values_json")
                .IsRequired();
            entity.Property(item => item.Version)
                .HasColumnName("version")
                .IsConcurrencyToken()
                .IsRequired();
            entity.Property(item => item.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .IsRequired();
            entity.Property(item => item.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(item => item.RestartRequired)
                .HasColumnName("restart_required")
                .IsRequired();
        });

        return modelBuilder;
    }
}

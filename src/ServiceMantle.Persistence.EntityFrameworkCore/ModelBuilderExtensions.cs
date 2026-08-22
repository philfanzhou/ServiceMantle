using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Adds ServiceMantle entity configuration to application models.
/// </summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Adds the ServiceMantle service installation entity to the model.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder for fluent use.</returns>
    public static ModelBuilder AddServiceMantleInstallation(this ModelBuilder modelBuilder)
    {
        if (modelBuilder is null)
        {
            throw new ArgumentNullException(nameof(modelBuilder));
        }

        modelBuilder.Entity<ServiceInstallationEntity>(entity =>
        {
            entity.ToTable("service_installations");

            entity.HasKey(item => item.ServiceId);

            entity.Property(item => item.ServiceId)
                .HasColumnName("service_id")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(item => item.Status)
                .HasColumnName("status")
                .HasConversion<int>()
                .IsRequired();

            entity.Property(item => item.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .IsRequired();

            entity.Property(item => item.CompletedAtUtc)
                .HasColumnName("completed_at_utc");

            entity.Property(item => item.Version)
                .HasColumnName("version")
                .IsRequired()
                .IsConcurrencyToken();
        });

        return modelBuilder;
    }
}

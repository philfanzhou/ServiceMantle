using Microsoft.EntityFrameworkCore;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>Adds encrypted ASP.NET Core Data Protection key persistence to consuming EF Core models.</summary>
public static class DataProtectionKeyModelBuilderExtensions
{
    /// <summary>Adds the provider-agnostic <c>service_data_protection_keys</c> mapping.</summary>
    public static ModelBuilder AddServiceMantleDataProtectionKeys(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DataProtectionKeyEntity>(entity =>
        {
            entity.ToTable("service_data_protection_keys");
            entity.HasKey(item => new { item.ServiceId, item.KeyId });
            entity.Property(item => item.ServiceId)
                .HasColumnName("service_id")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(item => item.KeyId)
                .HasColumnName("key_id")
                .HasMaxLength(36)
                .IsFixedLength()
                .IsRequired();
            entity.Property(item => item.EncryptedXml)
                .HasColumnName("encrypted_xml")
                .IsRequired();
        });

        return modelBuilder;
    }
}

namespace ServiceMantle.Persistence.Relational.DataProtection;

internal sealed class DataProtectionKeyEntity
{
    public required string ServiceId { get; set; }

    public required string KeyId { get; set; }

    public required string EncryptedXml { get; set; }
}

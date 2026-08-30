namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>Defines stable, non-sensitive Data Protection key repository failure codes.</summary>
public static class WellKnownDataProtectionKeyRepositoryErrorCodes
{
    /// <summary>The supplied XML is not a supported Data Protection key element.</summary>
    public const string InvalidElement = "data_protection_keys.invalid_element";

    /// <summary>The key identifier already exists for this service.</summary>
    public const string DuplicateKey = "data_protection_keys.duplicate_key";

    /// <summary>The external root key could not be obtained or used for a write.</summary>
    public const string RootKeyUnavailable = "data_protection_keys.root_key_unavailable";

    /// <summary>A stored key could not be authenticated, decrypted, or parsed.</summary>
    public const string DecryptionFailed = "data_protection_keys.decryption_failed";

    /// <summary>The persistence operation failed for an unclassified storage reason.</summary>
    public const string StorageError = "data_protection_keys.storage_error";
}

/// <summary>Indicates a safe encrypted Data Protection key repository failure.</summary>
public sealed class DataProtectionKeyRepositoryException : Exception
{
    internal DataProtectionKeyRepositoryException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable, non-sensitive failure classification.</summary>
    public string ErrorCode { get; }

    /// <summary>Returns only the stable code and safe message.</summary>
    public override string ToString() =>
        $"DataProtectionKeyRepositoryException(ErrorCode={ErrorCode}, Message={Message})";
}

namespace ServiceMantle.AspNetCore;

/// <summary>Defines stable sensitive-Header startup configuration error codes.</summary>
public static class WellKnownSensitiveHeaderConfigurationErrorCodes
{
    /// <summary>A configured Header name is not a valid HTTP token.</summary>
    public const string InvalidName = "sensitive_headers.invalid_name";

    /// <summary>A configured Header-name collection could not be enumerated safely.</summary>
    public const string EnumerationFailed = "sensitive_headers.enumeration_failed";

    /// <summary>The configuration callback failed.</summary>
    public const string ConfigureFailed = "sensitive_headers.configure_failed";

    /// <summary>A separately registered sanitizer conflicts with the owned immutable snapshot.</summary>
    public const string SanitizerConflict = "sensitive_headers.sanitizer_conflict";
}

/// <summary>Indicates a safe startup failure in the sensitive request Header boundary.</summary>
public sealed class ServiceMantleSensitiveHeaderConfigurationException : Exception
{
    internal ServiceMantleSensitiveHeaderConfigurationException(string errorCode, string fieldName)
        : base($"The ServiceMantle sensitive Header configuration is invalid for {fieldName} ({errorCode}).")
    {
        ErrorCode = errorCode;
        FieldName = fieldName;
    }

    /// <summary>Gets the stable, non-sensitive failure classification.</summary>
    public string ErrorCode { get; }

    /// <summary>Gets the configuration field that failed validation.</summary>
    public string FieldName { get; }

    /// <summary>Returns only stable configuration metadata.</summary>
    public override string ToString() =>
        $"ServiceMantleSensitiveHeaderConfigurationException(ErrorCode={ErrorCode}, FieldName={FieldName})";
}

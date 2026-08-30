namespace ServiceMantle.AspNetCore;

/// <summary>Defines stable forwarded-header startup configuration error codes.</summary>
public static class WellKnownForwardedHeadersConfigurationErrorCodes
{
    /// <summary>A configuration collection could not be enumerated safely.</summary>
    public const string EnumerationFailed = "forwarded_headers.enumeration_failed";

    /// <summary>The forwarding limit is null or outside the supported range.</summary>
    public const string InvalidForwardLimit = "forwarded_headers.invalid_forward_limit";

    /// <summary>No explicit trusted proxy or network was supplied.</summary>
    public const string TrustedProxyRequired = "forwarded_headers.trusted_proxy_required";

    /// <summary>A configured address, network, or host is invalid.</summary>
    public const string InvalidValue = "forwarded_headers.invalid_value";

    /// <summary>A configuration collection contains a normalized duplicate.</summary>
    public const string DuplicateValue = "forwarded_headers.duplicate_value";

    /// <summary>Multiple registrations do not describe the same normalized boundary.</summary>
    public const string ConflictingRegistration = "forwarded_headers.conflicting_registration";
}

/// <summary>Indicates a safe startup failure in the forwarded-header trust boundary.</summary>
public sealed class ServiceMantleForwardedHeadersConfigurationException : Exception
{
    internal ServiceMantleForwardedHeadersConfigurationException(string errorCode, string fieldName)
        : base($"The ServiceMantle forwarded-header configuration is invalid for {fieldName} ({errorCode}).")
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
        $"ServiceMantleForwardedHeadersConfigurationException(ErrorCode={ErrorCode}, FieldName={FieldName})";
}

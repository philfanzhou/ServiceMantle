namespace ServiceMantle.Configuration;

/// <summary>
/// Defines stable error codes returned by sensitive-value protection failures.
/// </summary>
public static class WellKnownSensitiveValueProtectionErrorCodes
{
    /// <summary>
    /// The protected value does not use a supported envelope format.
    /// </summary>
    public const string InvalidCiphertext = "sensitive_value.invalid_ciphertext";

    /// <summary>
    /// The protected value declares an unsupported envelope version.
    /// </summary>
    public const string UnsupportedVersion = "sensitive_value.unsupported_version";

    /// <summary>
    /// The protected value could not be authenticated in the requested context.
    /// </summary>
    public const string AuthenticationFailed = "sensitive_value.authentication_failed";
}

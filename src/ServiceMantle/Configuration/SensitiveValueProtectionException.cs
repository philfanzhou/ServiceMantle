namespace ServiceMantle.Configuration;

/// <summary>
/// Indicates that a protected sensitive value could not be safely decoded or authenticated.
/// </summary>
public sealed class SensitiveValueProtectionException : Exception
{
    internal SensitiveValueProtectionException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a stable, non-sensitive classification for the failure.
    /// </summary>
    public string ErrorCode { get; }
}

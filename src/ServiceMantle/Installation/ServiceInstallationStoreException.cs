namespace ServiceMantle.Installation;

/// <summary>
/// Indicates installation persistence failures with a safe error code.
/// </summary>
public sealed class ServiceInstallationStoreException : Exception
{
    public ServiceInstallationStoreException(
        string errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the stable error code for this installation failure.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Returns a sanitized installation exception message.
    /// </summary>
    public override string ToString() =>
        $"ServiceInstallationStoreException(ErrorCode={ErrorCode}, Message={Message})";
}

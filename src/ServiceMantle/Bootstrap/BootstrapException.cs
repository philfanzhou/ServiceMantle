namespace ServiceMantle.Bootstrap;

/// <summary>
/// Indicates that a bootstrap file could not be read or safely written.
/// </summary>
public sealed class BootstrapException : Exception
{
    internal BootstrapException(
        string filePath,
        string message,
        Exception? innerException = null,
        BootstrapFileFailureKind failureKind = BootstrapFileFailureKind.Unavailable)
        : base(message, innerException)
    {
        FilePath = filePath;
        FailureKind = failureKind;
    }

    /// <summary>
    /// Gets the absolute bootstrap file path associated with the failure.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the stable machine-readable reason this bootstrap file operation failed.
    /// </summary>
    /// <remarks>
    /// This is the only part of the exception a consumer may branch on. <see cref="Exception.Message"/>,
    /// <see cref="FilePath"/>, and <see cref="Exception.InnerException"/> remain diagnostic detail
    /// for local logs, are not part of any wire contract, and must not be projected outward.
    /// </remarks>
    public BootstrapFileFailureKind FailureKind { get; }
}

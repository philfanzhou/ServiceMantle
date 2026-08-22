namespace ServiceMantle.Bootstrap;

/// <summary>
/// Indicates that a bootstrap file could not be read or safely written.
/// </summary>
public sealed class BootstrapException : Exception
{
    internal BootstrapException(string filePath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Gets the absolute bootstrap file path associated with the failure.
    /// </summary>
    public string FilePath { get; }
}

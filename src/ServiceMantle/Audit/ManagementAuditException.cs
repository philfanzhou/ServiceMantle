namespace ServiceMantle.Audit;

/// <summary>
/// Indicates a management audit domain or persistence failure with a stable error code.
/// </summary>
public sealed class ManagementAuditException : Exception
{
    public ManagementAuditException(
        string errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the stable error code for this audit failure.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Returns a sanitized exception projection that never includes audit content.
    /// </summary>
    public override string ToString() =>
        $"ManagementAuditException(ErrorCode={ErrorCode}, Message={Message})";
}

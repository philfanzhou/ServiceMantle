namespace ServiceMantle.Migration;

/// <summary>
/// Indicates a safe migration lock failure without exposing provider details or secrets.
/// </summary>
public sealed class DatabaseMigrationLockException : Exception
{
    /// <summary>
    /// Initializes a migration lock exception with a safe error code.
    /// </summary>
    /// <param name="errorCode">A safe error code from WellKnownMigrationErrorCodes or similar.</param>
    /// <param name="message">A safe message that does not contain connection strings or secrets.</param>
    public DatabaseMigrationLockException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the safe error code for this lock failure.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Returns only safe information without exposing secrets.
    /// </summary>
    public override string ToString() =>
        $"DatabaseMigrationLockException(ErrorCode={ErrorCode}, Message={Message})";
}

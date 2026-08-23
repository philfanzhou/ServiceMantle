namespace ServiceMantle.Bootstrap;

/// <summary>
/// Describes what a provider learned while inspecting a database target without modifying it.
/// </summary>
public enum DatabaseTargetObservationStatus
{
    /// <summary>
    /// The database server itself could not be reached or refused the supplied credentials.
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// The server is reachable, but the named target does not exist yet.
    /// </summary>
    TargetMissing,

    /// <summary>
    /// The server is reachable and the target exists, but a connection to the target itself failed.
    /// </summary>
    TargetUnreachable,

    /// <summary>
    /// The server is reachable, the target exists, and a connection to the target succeeded.
    /// </summary>
    TargetConnectable
}

/// <summary>
/// Represents the safe, structured outcome of inspecting a database target: whether the server
/// can be reached, whether the target exists, and whether the target itself can be connected to.
/// Never carries connection strings or other secret values.
/// </summary>
public sealed class DatabaseTargetObservation
{
    private DatabaseTargetObservation(DatabaseTargetObservationStatus status, string? errorCode)
    {
        Status = status;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the observed target status.
    /// </summary>
    public DatabaseTargetObservationStatus Status { get; }

    /// <summary>
    /// Gets the safe error code describing why the server or target could not be reached, or null
    /// when the observation did not encounter a failure.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets a value indicating whether the database server accepted a connection.
    /// </summary>
    public bool IsServerReachable => Status != DatabaseTargetObservationStatus.ServerUnreachable;

    /// <summary>
    /// Gets a value indicating whether the named target is not known to be missing. This is
    /// conservative: for <see cref="DatabaseTargetObservationStatus.TargetUnreachable"/>, existence
    /// could not be confirmed either way, so it is not reported as missing.
    /// </summary>
    public bool TargetExists =>
        Status is DatabaseTargetObservationStatus.TargetUnreachable or DatabaseTargetObservationStatus.TargetConnectable;

    /// <summary>
    /// Gets a value indicating whether a connection to the target itself succeeded.
    /// </summary>
    public bool IsTargetConnectable => Status == DatabaseTargetObservationStatus.TargetConnectable;

    /// <summary>
    /// Creates an observation indicating the server could not be reached.
    /// </summary>
    /// <param name="errorCode">A safe, stable error code describing the failure.</param>
    public static DatabaseTargetObservation ServerUnreachable(string errorCode) =>
        new(DatabaseTargetObservationStatus.ServerUnreachable, RequireErrorCode(errorCode));

    /// <summary>
    /// Creates an observation indicating the server is reachable but the target does not exist.
    /// </summary>
    public static DatabaseTargetObservation TargetMissing() =>
        new(DatabaseTargetObservationStatus.TargetMissing, null);

    /// <summary>
    /// Creates an observation indicating the target exists but could not itself be connected to.
    /// </summary>
    /// <param name="errorCode">A safe, stable error code describing the failure.</param>
    public static DatabaseTargetObservation TargetUnreachable(string errorCode) =>
        new(DatabaseTargetObservationStatus.TargetUnreachable, RequireErrorCode(errorCode));

    /// <summary>
    /// Creates an observation indicating the target exists and is connectable.
    /// </summary>
    public static DatabaseTargetObservation TargetConnectable() =>
        new(DatabaseTargetObservationStatus.TargetConnectable, null);

    /// <summary>
    /// Returns safe observation information without secret values.
    /// </summary>
    public override string ToString() =>
        ErrorCode is null
            ? $"DatabaseTargetObservation(Status={Status})"
            : $"DatabaseTargetObservation(Status={Status}, ErrorCode={ErrorCode})";

    private static string RequireErrorCode(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return errorCode;
    }
}

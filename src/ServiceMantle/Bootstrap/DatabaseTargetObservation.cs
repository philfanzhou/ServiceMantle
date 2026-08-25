namespace ServiceMantle.Bootstrap;

/// <summary>
/// Describes what a provider learned while inspecting a database target without modifying it.
/// </summary>
public enum DatabaseTargetObservationStatus
{
    /// <summary>
    /// The database server itself could not be reached or did not complete the connection protocol.
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// The server is reachable, but the named target does not exist yet.
    /// </summary>
    TargetMissing,

    /// <summary>
    /// The server is reachable, but a connection to the target itself failed. Target existence may
    /// be known or unknown depending on the stage at which the server rejected the connection.
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
    private DatabaseTargetObservation(
        DatabaseTargetObservationStatus status,
        bool? targetExists,
        string? errorCode)
    {
        Status = status;
        TargetExists = targetExists;
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
    /// Gets a value indicating whether the database server responded to the connection attempt.
    /// </summary>
    public bool IsServerReachable => Status != DatabaseTargetObservationStatus.ServerUnreachable;

    /// <summary>
    /// Gets whether the named target exists, or null when the observation could not establish
    /// existence. Authentication can fail before PostgreSQL checks the database name, so an
    /// authentication failure reports null rather than incorrectly claiming the target exists.
    /// </summary>
    public bool? TargetExists { get; }

    /// <summary>
    /// Gets a value indicating whether a connection to the target itself succeeded.
    /// </summary>
    public bool IsTargetConnectable => Status == DatabaseTargetObservationStatus.TargetConnectable;

    /// <summary>
    /// Creates an observation indicating the server could not be reached.
    /// </summary>
    /// <param name="errorCode">A safe, stable error code describing the failure.</param>
    public static DatabaseTargetObservation ServerUnreachable(string errorCode) =>
        new(
            DatabaseTargetObservationStatus.ServerUnreachable,
            null,
            DatabaseTargetPreparationErrorCode.Validate(errorCode, nameof(errorCode)));

    /// <summary>
    /// Creates an observation indicating the server is reachable but the target does not exist.
    /// </summary>
    public static DatabaseTargetObservation TargetMissing() =>
        new(DatabaseTargetObservationStatus.TargetMissing, false, null);

    /// <summary>
    /// Creates an observation indicating the target could not itself be connected to.
    /// </summary>
    /// <param name="errorCode">A safe, stable error code describing the failure.</param>
    /// <param name="targetExists">
    /// true when the server proved the target exists; null when existence could not be established.
    /// </param>
    public static DatabaseTargetObservation TargetUnreachable(
        string errorCode,
        bool? targetExists = null)
    {
        if (targetExists == false)
        {
            throw new ArgumentException(
                "A target known to be missing must use the TargetMissing observation.",
                nameof(targetExists));
        }

        return new DatabaseTargetObservation(
            DatabaseTargetObservationStatus.TargetUnreachable,
            targetExists,
            DatabaseTargetPreparationErrorCode.Validate(errorCode, nameof(errorCode)));
    }

    /// <summary>
    /// Creates an observation indicating the target exists and is connectable.
    /// </summary>
    public static DatabaseTargetObservation TargetConnectable() =>
        new(DatabaseTargetObservationStatus.TargetConnectable, true, null);

    /// <summary>
    /// Returns safe observation information without secret values.
    /// </summary>
    public override string ToString() =>
        ErrorCode is null
            ? $"DatabaseTargetObservation(Status={Status})"
            : $"DatabaseTargetObservation(Status={Status}, ErrorCode={ErrorCode})";
}

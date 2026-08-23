namespace ServiceMantle.Bootstrap;

/// <summary>
/// Describes what happened to the target as a result of a successful preparation call.
/// </summary>
public enum DatabaseTargetPreparationOutcome
{
    /// <summary>
    /// The target did not exist and was created by this call.
    /// </summary>
    Created,

    /// <summary>
    /// The target already existed. No destructive or modifying action was taken.
    /// </summary>
    AlreadyExists
}

/// <summary>
/// Represents the safe, structured result of a database target preparation attempt.
/// Never carries connection strings or other secret values.
/// </summary>
public sealed class DatabaseTargetPreparationResult
{
    private DatabaseTargetPreparationResult(
        bool succeeded,
        DatabaseTargetPreparationOutcome? outcome,
        string? errorCode)
    {
        Succeeded = succeeded;
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a value indicating whether the target is now known to be ready.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the outcome of a successful preparation, or null when the attempt failed.
    /// </summary>
    public DatabaseTargetPreparationOutcome? Outcome { get; }

    /// <summary>
    /// Gets the safe failure error code, or null when the attempt succeeded.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a successful preparation result.
    /// </summary>
    /// <param name="outcome">Whether the target was newly created or already existed.</param>
    public static DatabaseTargetPreparationResult Success(DatabaseTargetPreparationOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        return new DatabaseTargetPreparationResult(true, outcome, null);
    }

    /// <summary>
    /// Creates a failed preparation result with a safe error code.
    /// </summary>
    /// <param name="errorCode">A registered <see cref="WellKnownDatabaseTargetPreparationErrorCodes"/> value.</param>
    public static DatabaseTargetPreparationResult Failure(string errorCode)
    {
        var safeErrorCode = DatabaseTargetPreparationErrorCode.Validate(errorCode, nameof(errorCode));
        return new DatabaseTargetPreparationResult(false, null, safeErrorCode);
    }

    /// <summary>
    /// Returns safe result information without secret values.
    /// </summary>
    public override string ToString() =>
        Succeeded
            ? $"DatabaseTargetPreparationResult(Succeeded=True, Outcome={Outcome})"
            : $"DatabaseTargetPreparationResult(Succeeded=False, ErrorCode={ErrorCode})";
}

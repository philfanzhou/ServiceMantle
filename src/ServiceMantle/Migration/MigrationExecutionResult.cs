namespace ServiceMantle.Migration;

/// <summary>
/// Represents the safe, immutable result of a migration orchestration session.
/// All fields are non-secret and safe to log or display.
/// </summary>
public sealed class MigrationExecutionResult
{
    private MigrationExecutionResult(
        bool succeeded,
        string? errorCode,
        string? errorMessage,
        bool executorWasCalled)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ExecutorWasCalled = executorWasCalled;
    }

    /// <summary>
    /// Gets a value indicating whether the migration orchestration succeeded.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the safe error code when orchestration failed, or null when it succeeded.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets the safe error message when orchestration failed, or null when it succeeded.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets a value indicating whether the consuming service's migration executor was invoked.
    /// When the database was already at the current compatible version, the executor is not called.
    /// </summary>
    public bool ExecutorWasCalled { get; }

    /// <summary>
    /// Creates a successful migration result.
    /// </summary>
    public static MigrationExecutionResult Success(bool executorWasCalled) =>
        new(succeeded: true, errorCode: null, errorMessage: null, executorWasCalled);

    /// <summary>
    /// Creates a failed migration result with a safe error code and message.
    /// </summary>
    /// <param name="errorCode">A safe, well-known error code. Must not be null or whitespace.</param>
    /// <param name="errorMessage">A safe message without secrets. Must not be null or whitespace.</param>
    /// <param name="executorWasCalled">Whether the consuming service executor was invoked.</param>
    public static MigrationExecutionResult Failure(
        string errorCode,
        string errorMessage,
        bool executorWasCalled = false)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code must not be null or whitespace.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Error message must not be null or whitespace.", nameof(errorMessage));
        }

        return new(succeeded: false, errorCode, errorMessage, executorWasCalled);
    }

    /// <summary>
    /// Returns safe result information without exposing secrets.
    /// </summary>
    public override string ToString() =>
        Succeeded
            ? $"MigrationExecutionResult(Succeeded=True, ExecutorWasCalled={ExecutorWasCalled})"
            : $"MigrationExecutionResult(Succeeded=False, ErrorCode={ErrorCode}, ExecutorWasCalled={ExecutorWasCalled})";
}

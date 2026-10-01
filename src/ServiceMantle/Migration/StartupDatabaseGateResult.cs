namespace ServiceMantle.Migration;

/// <summary>
/// The finite outcome of one <see cref="StartupDatabaseGate"/> run. It carries only a success
/// flag, a safe error code from the ServiceMantle allow lists, and whether the consuming
/// service's migration executor ran; never connection strings or driver messages.
/// </summary>
public sealed class StartupDatabaseGateResult
{
    private StartupDatabaseGateResult(bool succeeded, string? errorCode, bool executorWasCalled)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ExecutorWasCalled = executorWasCalled;
    }

    /// <summary>Gets whether the gate completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe failure error code, or null on success.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets whether the consuming service's migration executor was invoked, as reported by the
    /// migration orchestration.
    /// </summary>
    public bool ExecutorWasCalled { get; }

    /// <summary>Creates a successful gate result.</summary>
    /// <param name="executorWasCalled">Whether the migration executor was invoked.</param>
    public static StartupDatabaseGateResult Success(bool executorWasCalled) =>
        new(true, null, executorWasCalled);

    /// <summary>Creates a failed gate result with a safe error code.</summary>
    /// <param name="errorCode">A safe error code from the ServiceMantle allow lists.</param>
    /// <param name="executorWasCalled">Whether the migration executor was invoked.</param>
    public static StartupDatabaseGateResult Failure(string errorCode, bool executorWasCalled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new(false, errorCode, executorWasCalled);
    }

    /// <summary>Returns only the finite outcome, with no target or connection information.</summary>
    public override string ToString() =>
        Succeeded
            ? $"StartupDatabaseGateResult(Succeeded=True, ExecutorWasCalled={ExecutorWasCalled})"
            : $"StartupDatabaseGateResult(Succeeded=False, ErrorCode={ErrorCode}, ExecutorWasCalled={ExecutorWasCalled})";
}

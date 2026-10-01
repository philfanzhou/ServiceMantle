using ServiceMantle.Health;

namespace ServiceMantle.Migration;

/// <summary>
/// The process-local, one-way receipt of the startup database gate. It records whether the
/// deployment validation, optional target preparation, and migration orchestration of this
/// process completed successfully.
/// </summary>
/// <remarks>
/// <para>
/// The state machine is fixed and one-way: <see cref="ServiceMigrationReadinessState.NotStarted"/>
/// to <see cref="ServiceMigrationReadinessState.Running"/>, then to exactly one of
/// <see cref="ServiceMigrationReadinessState.Succeeded"/> or
/// <see cref="ServiceMigrationReadinessState.Failed"/>. Terminal states never change again; a
/// caller cancellation leaves the receipt in <see cref="ServiceMigrationReadinessState.Running"/>
/// rather than recording success. Reads and writes are safe under concurrent access.
/// </para>
/// <para>
/// The receipt is process-local and in-memory only. It makes no statement about other processes,
/// survives no restart, and is not a coordination primitive: the gate itself runs at most once
/// per receipt instance.
/// </para>
/// </remarks>
public sealed class StartupDatabaseReceipt
{
    private const int NotStarted = 0;
    private const int Running = 1;
    private const int Succeeded = 2;
    private const int Failed = 3;

    private int state = NotStarted;
    private string? errorCode;

    /// <summary>Gets the current receipt state.</summary>
    public ServiceMigrationReadinessState State => StateCode switch
    {
        Running => ServiceMigrationReadinessState.Running,
        Succeeded => ServiceMigrationReadinessState.Succeeded,
        Failed => ServiceMigrationReadinessState.Failed,
        _ => ServiceMigrationReadinessState.NotStarted,
    };

    /// <summary>
    /// Gets the safe failure error code recorded with the failed terminal state, or null while
    /// the receipt has not failed.
    /// </summary>
    public string? ErrorCode => Volatile.Read(ref errorCode);

    /// <summary>
    /// Attempts to move the receipt from <see cref="ServiceMigrationReadinessState.NotStarted"/>
    /// to <see cref="ServiceMigrationReadinessState.Running"/>. Returns false when the receipt
    /// already started or completed; the gate treats that as a repeated invocation.
    /// </summary>
    public bool TryMarkRunning() => Interlocked.CompareExchange(ref state, Running, NotStarted) == NotStarted;

    /// <summary>
    /// Attempts to complete the receipt successfully from
    /// <see cref="ServiceMigrationReadinessState.Running"/>. Returns false when the receipt is
    /// not in the running state.
    /// </summary>
    public bool TryCompleteSucceeded() => Interlocked.CompareExchange(ref state, Succeeded, Running) == Running;

    /// <summary>
    /// Attempts to complete the receipt as failed from
    /// <see cref="ServiceMigrationReadinessState.Running"/>. Returns false when the receipt is
    /// not in the running state. The error code must be a non-empty safe code without control
    /// characters; it is recorded only when the transition succeeds.
    /// </summary>
    /// <param name="failureErrorCode">A safe error code from the ServiceMantle allow lists.</param>
    public bool TryCompleteFailed(string failureErrorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureErrorCode);
        if (failureErrorCode.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The failure error code cannot contain control characters.",
                nameof(failureErrorCode));
        }

        if (Interlocked.CompareExchange(ref state, Failed, Running) != Running)
        {
            return false;
        }

        Volatile.Write(ref errorCode, failureErrorCode);
        return true;
    }

    private int StateCode => Volatile.Read(ref state);
}

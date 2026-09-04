using ServiceMantle.Migration;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Test double for IDatabaseMigrationExecutor with configurable behavior.
/// Supports sequential state changes and shared database state for concurrent testing.
/// </summary>
internal sealed class FakeMigrationExecutor : IDatabaseMigrationExecutor
{
    private readonly MigrationObservationState? singleState;
    private readonly MigrationObservationState[]? sequentialStates;
    private readonly Exception? inspectException;
    private readonly Exception? executeException;
    private readonly Exception? inspectExceptionAtCall;
    private readonly int inspectExceptionCallNumber;
    private readonly Func<CancellationToken, Task>? executeDelay;
    private readonly Func<int, CancellationToken, Task>? inspectDelay;
    private readonly bool ignoreCancellationAfterExecuteDelay;
    private int inspectCallIndex;

    public int InspectCallCount { get; private set; }
    public int ExecuteCallCount { get; private set; }

    /// <summary>
    /// Single-state executor: always returns the same state on every Inspect.
    /// </summary>
    public FakeMigrationExecutor(
        MigrationObservationState singleState = MigrationObservationState.Empty,
        Exception? inspectException = null,
        Exception? executeException = null,
        Func<CancellationToken, Task>? executeDelay = null,
        Func<int, CancellationToken, Task>? inspectDelay = null,
        bool ignoreCancellationAfterExecuteDelay = false)
    {
        this.singleState = singleState;
        this.inspectException = inspectException;
        this.executeException = executeException;
        this.executeDelay = executeDelay;
        this.inspectDelay = inspectDelay;
        this.ignoreCancellationAfterExecuteDelay = ignoreCancellationAfterExecuteDelay;
    }

    /// <summary>
    /// Sequential-state executor: returns different states on successive Inspect calls.
    /// Useful for testing "before migration" and "after migration" states.
    /// </summary>
    /// <param name="inspectExceptionAtCall">
    /// Optional exception to throw when InspectAsync is called for the
    /// <paramref name="inspectExceptionCallNumber"/>-th time (1-based), instead of returning
    /// the corresponding value from <paramref name="sequentialStates"/>. Lets a test simulate
    /// a successful first inspection followed by a failing re-inspection (e.g. after execution).
    /// </param>
    public FakeMigrationExecutor(
        MigrationObservationState[] sequentialStates,
        Exception? inspectException = null,
        Exception? executeException = null,
        Func<CancellationToken, Task>? executeDelay = null,
        Exception? inspectExceptionAtCall = null,
        int inspectExceptionCallNumber = 0,
        Func<int, CancellationToken, Task>? inspectDelay = null,
        bool ignoreCancellationAfterExecuteDelay = false)
    {
        ArgumentNullException.ThrowIfNull(sequentialStates);
        if (sequentialStates.Length == 0)
        {
            throw new ArgumentException("Sequential states must not be empty.", nameof(sequentialStates));
        }

        this.sequentialStates = sequentialStates;
        this.inspectException = inspectException;
        this.executeException = executeException;
        this.executeDelay = executeDelay;
        this.inspectExceptionAtCall = inspectExceptionAtCall;
        this.inspectExceptionCallNumber = inspectExceptionCallNumber;
        this.inspectDelay = inspectDelay;
        this.ignoreCancellationAfterExecuteDelay = ignoreCancellationAfterExecuteDelay;
    }

    public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
    {
        InspectCallCount++;
        cancellationToken.ThrowIfCancellationRequested();

        if (inspectDelay is not null)
        {
            await inspectDelay(InspectCallCount, cancellationToken).ConfigureAwait(false);
        }

        if (inspectException is not null)
        {
            throw inspectException;
        }

        if (inspectExceptionAtCall is not null && InspectCallCount == inspectExceptionCallNumber)
        {
            throw inspectExceptionAtCall;
        }

        MigrationObservationState state;
        if (singleState.HasValue)
        {
            state = singleState.Value;
        }
        else if (sequentialStates is not null)
        {
            if (inspectCallIndex >= sequentialStates.Length)
            {
                throw new InvalidOperationException(
                    $"Unexpected number of Inspect calls. Expected at most {sequentialStates.Length}, got {InspectCallCount}.");
            }
            state = sequentialStates[inspectCallIndex++];
        }
        else
        {
            state = MigrationObservationState.Empty;
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
        return state;
    }

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        cancellationToken.ThrowIfCancellationRequested();

        if (executeDelay is not null)
        {
            await executeDelay(cancellationToken).ConfigureAwait(false);
        }

        if (!ignoreCancellationAfterExecuteDelay)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (executeException is not null)
        {
            throw executeException;
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

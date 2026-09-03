using ServiceMantle.Bootstrap;

namespace ServiceMantle.Migration;

internal static class SingleInstanceMigrationOrchestration
{
    // Shared across orchestrator and registry instances. A turn is scoped to the target, not the
    // service ID, credentials, or consuming executor instance. Entries include waiting callers.
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Provider, string Target), Entry> Entries = new();

    internal static async ValueTask<MigrationExecutionResult> RunAsync(
        IDatabaseMigrationExecutor executor,
        DatabaseDeploymentCapabilityRegistry.Registration registration,
        BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken callerToken)
    {
        if (acquireTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1D))
            throw new ArgumentOutOfRangeException(nameof(acquireTimeout), "The single-instance acquire timeout is too large.");
        using var timeout = new CancellationTokenSource(acquireTimeout);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token);
        Entry? entry = null;
        (string Provider, string Target) key = default;
        var acquired = false;
        try
        {
            var identity = await registration.Provider.GetCanonicalTargetIdentityAsync(bootstrap, acquisition.Token)
                .ConfigureAwait(false);
            acquisition.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(identity))
                return Failure(WellKnownMigrationErrorCodes.LockNotSupported, "The provider did not supply a canonical target identity.");

            key = (registration.Capability.ProviderId.ToUpperInvariant(), identity);
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    Entries.Add(key, entry);
                }
                entry.References++;
            }

            await entry.Gate.WaitAsync(acquisition.Token).ConfigureAwait(false);
            acquired = true;
            acquisition.Token.ThrowIfCancellationRequested();
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            // Only caller cancellation governs execution after the local turn was acquired.
            return await ExecuteStagesAsync(executor, callerToken).ConfigureAwait(false);
        }
        catch (Exception) when (callerToken.IsCancellationRequested)
        {
            throw SafeCancellation(callerToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            return Failure(WellKnownMigrationErrorCodes.LockTimeout, "Waiting for the single-instance migration turn timed out.");
        }
        catch (Exception)
        {
            return Failure(WellKnownMigrationErrorCodes.LockFailed, "The single-instance migration turn could not be acquired.");
        }
        finally
        {
            if (entry is not null)
            {
                lock (Sync)
                {
                    if (acquired) entry.Gate.Release();
                    if (--entry.References == 0)
                    {
                        Entries.Remove(key);
                        entry.Gate.Dispose();
                    }
                }
            }
        }
    }

    private static async ValueTask<MigrationExecutionResult> ExecuteStagesAsync(
        IDatabaseMigrationExecutor executor, CancellationToken callerToken)
    {
        var executed = false;
        var failureCode = WellKnownMigrationErrorCodes.InspectionFailed;
        try
        {
            callerToken.ThrowIfCancellationRequested();
            var initial = await executor.InspectAsync(callerToken).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            if (initial == MigrationObservationState.CurrentVersionCompatible)
                return MigrationExecutionResult.Success(false);
            if (initial == MigrationObservationState.VersionTooNew)
                return Failure(WellKnownMigrationErrorCodes.VersionTooNew, "The database schema version is newer than this application.");
            if (initial is not (MigrationObservationState.Empty or MigrationObservationState.PendingMigration))
                return Failure(WellKnownMigrationErrorCodes.InspectionFailed, "Database state inspection failed.");

            failureCode = WellKnownMigrationErrorCodes.ExecutionFailed;
            executed = true;
            await executor.ExecuteAsync(callerToken).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            failureCode = WellKnownMigrationErrorCodes.InspectionFailed;
            var final = await executor.InspectAsync(callerToken).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            return final == MigrationObservationState.CurrentVersionCompatible
                ? MigrationExecutionResult.Success(true)
                : Failure(WellKnownMigrationErrorCodes.FinalStateInvalid, "The final database state is incompatible.", true);
        }
        catch (Exception) when (callerToken.IsCancellationRequested)
        {
            throw SafeCancellation(callerToken);
        }
        catch (Exception)
        {
            // Internal cancellation is a stage failure, never caller cancellation.
            return Failure(failureCode, "The single-instance migration stage failed.", executed);
        }
    }

    private static OperationCanceledException SafeCancellation(CancellationToken token) =>
        new("Single-instance migration was cancelled by the caller.", token);

    private static MigrationExecutionResult Failure(string code, string message, bool executed = false) =>
        MigrationExecutionResult.Failure(code, message, executed);

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int References;
    }
}

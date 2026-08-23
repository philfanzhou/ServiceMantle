namespace ServiceMantle.Migration;

/// <summary>
/// Orchestrates multi-instance safe database migration under an optional provider-specific lock.
/// </summary>
public sealed class DatabaseMigrationOrchestrator
{
    private readonly IDatabaseMigrationExecutor executor;
    private readonly DatabaseMigrationLockProviderRegistry lockProviderRegistry;

    /// <summary>
    /// Initializes a migration orchestrator.
    /// </summary>
    /// <param name="executor">The consuming service's migration executor.</param>
    /// <param name="lockProviderRegistry">The registry of available lock providers.</param>
    public DatabaseMigrationOrchestrator(
        IDatabaseMigrationExecutor executor,
        DatabaseMigrationLockProviderRegistry lockProviderRegistry)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.lockProviderRegistry = lockProviderRegistry ?? throw new ArgumentNullException(nameof(lockProviderRegistry));
    }

    /// <summary>
    /// Orchestrates the migration workflow with the following semantics:
    /// 1. Validate parameters and check for caller cancellation.
    /// 2. Resolve and acquire the provider-specific migration lock based on service ID.
    /// 3. After acquiring the lock, re-inspect the database state.
    /// 4. If already at the current compatible version, return success without calling the executor.
    /// 5. If the database version is newer than the application, fail closed without calling the executor.
    /// 6. If the database is empty or needs migration, call the executor exactly once.
    /// 7. After executor completion, re-inspect the database state.
    /// 8. Return success only if the final state is compatible with the current application version.
    /// 9. Always release the lock, even on failure or cancellation.
    /// </summary>
    /// <param name="serviceId">The service identifier for which to orchestrate migration.</param>
    /// <param name="bootstrap">The bootstrap configuration for lock acquisition.</param>
    /// <param name="lockAcquireTimeout">Maximum time to wait for lock acquisition.</param>
    /// <param name="cancellationToken">Cancellation token. Cancellation takes precedence over timeout.</param>
    /// <returns>A structured result indicating success or safe failure.</returns>
    public async ValueTask<MigrationExecutionResult> OrchestrateMigrationAsync(
        ServiceId serviceId,
        Bootstrap.BootstrapDatabaseConfiguration bootstrap,
        TimeSpan lockAcquireTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(bootstrap);

        if (lockAcquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lock acquire timeout must be positive.",
                nameof(lockAcquireTimeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Resolve the lock provider for this database provider.
        if (!lockProviderRegistry.TryGetProvider(bootstrap.Provider, out var lockProvider))
        {
            return MigrationExecutionResult.Failure(
                WellKnownMigrationErrorCodes.LockNotSupported,
                $"No migration lock provider is registered for database provider '{bootstrap.Provider}'.");
        }

        // Lock provider is not registered; we must fail closed rather than silently proceeding without locking.
        if (lockProvider is null)
        {
            return MigrationExecutionResult.Failure(
                WellKnownMigrationErrorCodes.LockNotSupported,
                $"Migration lock provider for '{bootstrap.Provider}' resolved to null.");
        }

        IDatabaseMigrationLock? lock_ = null;
        try
        {
            // Acquire the lock. This will fail if no lock capability is registered or if acquisition times out.
            try
            {
                lock_ = await lockProvider.AcquireAsync(
                    serviceId,
                    bootstrap,
                    lockAcquireTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseMigrationLockException lockException)
            {
                return MigrationExecutionResult.Failure(
                    lockException.ErrorCode,
                    lockException.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    $"Unexpected error during lock acquisition: {ex.GetType().Name}");
            }

            // Verify that the provider returned a valid lock (fail-closed if null).
            if (lock_ is null)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Migration lock provider returned null lease.");
            }

            // Authority check: re-inspect the database state under the lock.
            MigrationObservationState initialState;
            try
            {
                initialState = await executor.InspectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Failed to inspect database state after acquiring the migration lock.");
            }

            // Decision: do we need to call the executor?
            if (initialState == MigrationObservationState.CurrentVersionCompatible)
            {
                // Already at current version; skip execution. Another waiting instance will observe the same state.
                return MigrationExecutionResult.Success(executorWasCalled: false);
            }

            if (initialState == MigrationObservationState.VersionTooNew)
            {
                // Database version is newer than the application; this instance cannot proceed.
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.VersionTooNew,
                    "Database schema version is newer than the current application version.");
            }

            if (initialState == MigrationObservationState.InspectionFailed)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Database state inspection failed.");
            }

            // At this point, the database is empty or requires migration.
            // Call the executor exactly once.
            try
            {
                await executor.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.ExecutionFailed,
                    "The consuming service's migration executor failed.",
                    executorWasCalled: true);
            }

            // Authority check: re-inspect the final state while still holding the lock.
            MigrationObservationState finalState;
            try
            {
                finalState = await executor.InspectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Failed to inspect database state after migration execution.",
                    executorWasCalled: true);
            }

            // Success requires the database to be at the current compatible version.
            if (finalState == MigrationObservationState.CurrentVersionCompatible)
            {
                return MigrationExecutionResult.Success(executorWasCalled: true);
            }

            // Any other state after execution is a failure.
            return MigrationExecutionResult.Failure(
                WellKnownMigrationErrorCodes.FinalStateInvalid,
                $"Database state after migration is incompatible: {finalState}",
                executorWasCalled: true);
        }
        finally
        {
            // Always attempt to release the lock, even on failure or cancellation.
            if (lock_ is not null)
            {
                try
                {
                    await lock_.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress lock release errors to avoid masking the primary exception.
                    // The provider is responsible for eventual cleanup (e.g., session timeout).
                }
            }
        }
    }
}

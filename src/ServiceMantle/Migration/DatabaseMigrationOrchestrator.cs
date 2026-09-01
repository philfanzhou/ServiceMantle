namespace ServiceMantle.Migration;

/// <summary>
/// Orchestrates multi-instance safe database migration under an optional provider-specific lock.
/// </summary>
public sealed class DatabaseMigrationOrchestrator
{
    private readonly IDatabaseMigrationExecutor executor;
    private readonly DatabaseMigrationLockProviderRegistry lockProviderRegistry;

    /// <summary>Initializes a migration orchestrator.</summary>
    public DatabaseMigrationOrchestrator(
        IDatabaseMigrationExecutor executor,
        DatabaseMigrationLockProviderRegistry lockProviderRegistry)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.lockProviderRegistry = lockProviderRegistry ??
            throw new ArgumentNullException(nameof(lockProviderRegistry));
    }

    /// <summary>
    /// Acquires the provider lock, inspects under that authority, executes at most once when
    /// required, and succeeds only after a compatible final inspection. Caller cancellation takes
    /// precedence over detected lease loss; lease loss fails closed with
    /// <c>migration.lock_failed</c> and prevents any later stage from starting.
    /// </summary>
    /// <param name="serviceId">The service identifier for which to orchestrate migration.</param>
    /// <param name="bootstrap">The bootstrap configuration for lock acquisition.</param>
    /// <param name="lockAcquireTimeout">Maximum time to wait for lock acquisition.</param>
    /// <param name="cancellationToken">Cancellation token. Cancellation takes precedence over timeout and lease loss.</param>
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
        if (!lockProviderRegistry.TryGetProvider(bootstrap.Provider, out var lockProvider))
        {
            return MigrationExecutionResult.Failure(
                WellKnownMigrationErrorCodes.LockNotSupported,
                $"No migration lock provider is registered for database provider '{bootstrap.Provider}'.");
        }

        if (lockProvider is null)
        {
            return MigrationExecutionResult.Failure(
                WellKnownMigrationErrorCodes.LockNotSupported,
                $"Migration lock provider for '{bootstrap.Provider}' resolved to null.");
        }

        IDatabaseMigrationLock? lock_ = null;
        try
        {
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

            if (lock_ is null)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Migration lock provider returned null lease.");
            }

            using var authorityCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lock_.LeaseLost);
            var authorityToken = authorityCts.Token;

            var authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: false);
            if (authorityFailure is not null)
            {
                return authorityFailure;
            }

            MigrationObservationState initialState;
            try
            {
                initialState = await executor.InspectAsync(authorityToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lock_.LeaseLost.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaseLostFailure(executorWasCalled: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: false);
                return authorityFailure ?? MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Failed to inspect database state after acquiring the migration lock.");
            }

            authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: false);
            if (authorityFailure is not null)
            {
                return authorityFailure;
            }

            if (initialState == MigrationObservationState.CurrentVersionCompatible)
            {
                return MigrationExecutionResult.Success(executorWasCalled: false);
            }

            if (initialState == MigrationObservationState.VersionTooNew)
            {
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

            try
            {
                await executor.ExecuteAsync(authorityToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lock_.LeaseLost.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaseLostFailure(executorWasCalled: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: true);
                return authorityFailure ?? MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.ExecutionFailed,
                    "The consuming service's migration executor failed.",
                    executorWasCalled: true);
            }

            authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: true);
            if (authorityFailure is not null)
            {
                return authorityFailure;
            }

            MigrationObservationState finalState;
            try
            {
                finalState = await executor.InspectAsync(authorityToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lock_.LeaseLost.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaseLostFailure(executorWasCalled: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: true);
                return authorityFailure ?? MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Failed to inspect database state after migration execution.",
                    executorWasCalled: true);
            }

            authorityFailure = CheckAuthority(lock_, cancellationToken, executorWasCalled: true);
            if (authorityFailure is not null)
            {
                return authorityFailure;
            }

            return finalState == MigrationObservationState.CurrentVersionCompatible
                ? MigrationExecutionResult.Success(executorWasCalled: true)
                : MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.FinalStateInvalid,
                    $"Database state after migration is incompatible: {finalState}",
                    executorWasCalled: true);
        }
        finally
        {
            if (lock_ is not null)
            {
                try
                {
                    await lock_.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Release failure must not replace the primary result or cancellation.
                }
            }
        }
    }

    private static MigrationExecutionResult? CheckAuthority(
        IDatabaseMigrationLock lock_,
        CancellationToken cancellationToken,
        bool executorWasCalled)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return lock_.LeaseLost.IsCancellationRequested
            ? LeaseLostFailure(executorWasCalled)
            : null;
    }

    private static MigrationExecutionResult LeaseLostFailure(bool executorWasCalled) =>
        MigrationExecutionResult.Failure(
            WellKnownMigrationErrorCodes.LockFailed,
            "The migration lock lease was lost before orchestration completed.",
            executorWasCalled);
}

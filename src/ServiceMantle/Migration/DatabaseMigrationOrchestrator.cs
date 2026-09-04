using ServiceMantle.Bootstrap;

namespace ServiceMantle.Migration;

/// <summary>
/// Orchestrates multi-instance safe database migration under an optional provider-specific lock.
/// </summary>
public sealed class DatabaseMigrationOrchestrator
{
    private readonly IDatabaseMigrationExecutor executor;
    private readonly DatabaseMigrationLockProviderRegistry lockProviderRegistry;
    private readonly DatabaseDeploymentCapabilityRegistry? deploymentCapabilities;

    /// <summary>Initializes a migration orchestrator.</summary>
    public DatabaseMigrationOrchestrator(
        IDatabaseMigrationExecutor executor,
        DatabaseMigrationLockProviderRegistry lockProviderRegistry)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.lockProviderRegistry = lockProviderRegistry ??
            throw new ArgumentNullException(nameof(lockProviderRegistry));
    }

    /// <summary>Initializes an orchestrator with explicit deployment capabilities.</summary>
    public DatabaseMigrationOrchestrator(
        IDatabaseMigrationExecutor executor,
        DatabaseMigrationLockProviderRegistry lockProviderRegistry,
        DatabaseDeploymentCapabilityRegistry deploymentCapabilities)
        : this(executor, lockProviderRegistry)
    {
        ArgumentNullException.ThrowIfNull(deploymentCapabilities);
        this.deploymentCapabilities = deploymentCapabilities;
    }

    /// <summary>
    /// Validates the explicit deployment mode before I/O. SingleInstance serializes the provider's
    /// canonical target within this process without constructing a distributed lease. MultiInstance
    /// retains the existing real-lock orchestration contract.
    /// </summary>
    /// <remarks>
    /// For SingleInstance, the timeout cancels target-identity resolution and bounds the wait for
    /// the process-local turn; identity providers must honor cancellation. For MultiInstance it
    /// bounds lock acquisition. It does not time-limit execution.
    /// SingleInstance callers must own the deployment assumption; no cross-process exclusion,
    /// rollback, or protection from external target replacement is provided. The two-argument
    /// constructor registers no deployment capability, so this overload fails closed when used
    /// with it. The original overload always requires a real lock regardless of declarations.
    /// </remarks>
    public async ValueTask<MigrationExecutionResult> OrchestrateMigrationAsync(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration bootstrap,
        DatabaseDeploymentMode deploymentMode,
        TimeSpan lockAcquireTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (lockAcquireTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Lock acquire timeout must be positive.", nameof(lockAcquireTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (deploymentCapabilities is null ||
            !new DatabaseDeploymentValidator(deploymentCapabilities).Validate(bootstrap.Provider, deploymentMode).IsSupported)
        {
            return MigrationExecutionResult.Failure(WellKnownMigrationErrorCodes.LockNotSupported,
                "The database deployment mode is unspecified or unsupported.");
        }

        if (deploymentMode == DatabaseDeploymentMode.MultiInstance)
        {
            return await OrchestrateMigrationAsync(serviceId, bootstrap, lockAcquireTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        deploymentCapabilities.TryGetRegistration(bootstrap.Provider, out var registration);
        return await SingleInstanceMigrationOrchestration.RunAsync(
            executor, registration!, bootstrap, lockAcquireTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires the provider lock, inspects under that authority, executes at most once when
    /// required, and succeeds only after a compatible final inspection. Caller cancellation takes
    /// precedence over detected lease loss; lease loss fails closed with
    /// <c>migration.lock_failed</c> and prevents any later stage from starting. Provider or executor
    /// cancellation while the caller token remains active is classified as a safe stage failure.
    /// Only <see cref="MigrationObservationState.Empty"/> and
    /// <see cref="MigrationObservationState.PendingMigration"/> permit execution after the initial
    /// inspection; undefined states fail closed as an inspection failure.
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

        ThrowIfCallerCancellationRequested(cancellationToken);
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
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCallerCancellation(cancellationToken);
            }
            catch (DatabaseMigrationLockException lockException)
            {
                return MigrationExecutionResult.Failure(
                    lockException.ErrorCode,
                    lockException.Message);
            }
            catch (Exception)
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Migration lock acquisition failed.");
            }

            ThrowIfCallerCancellationRequested(cancellationToken);

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
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCallerCancellation(cancellationToken);
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

            if (initialState is not (MigrationObservationState.Empty or MigrationObservationState.PendingMigration))
            {
                return MigrationExecutionResult.Failure(
                    WellKnownMigrationErrorCodes.InspectionFailed,
                    "Database state inspection failed.");
            }

            try
            {
                await executor.ExecuteAsync(authorityToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCallerCancellation(cancellationToken);
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
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCallerCancellation(cancellationToken);
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
        ThrowIfCallerCancellationRequested(cancellationToken);
        return lock_.LeaseLost.IsCancellationRequested
            ? LeaseLostFailure(executorWasCalled)
            : null;
    }

    private static void ThrowIfCallerCancellationRequested(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw SafeCallerCancellation(cancellationToken);
        }
    }

    private static OperationCanceledException SafeCallerCancellation(CancellationToken cancellationToken) =>
        new("Migration orchestration was cancelled by the caller.", cancellationToken);

    private static MigrationExecutionResult LeaseLostFailure(bool executorWasCalled) =>
        MigrationExecutionResult.Failure(
            WellKnownMigrationErrorCodes.LockFailed,
            "The migration lock lease was lost before orchestration completed.",
            executorWasCalled);
}

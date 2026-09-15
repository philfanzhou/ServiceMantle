using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Installation;
using ServiceMantle.Migration;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// Runs the sample's opt-in PostgreSQL startup deployment gate: observe the target, prepare it
/// only when that was explicitly permitted, migrate under the real advisory lock, and resolve the
/// startup phase from the installation row.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed and fails closed. A missing target is not created unless the consumer said
/// so, and a target that is present but unusable is never repaired. After a successful
/// preparation the target is not observed again: whether it is empty is decided by the
/// lock-held inspection inside the orchestration, never by a second unserialized observation.
/// </para>
/// <para>
/// Concurrency is serialized by the provider's real session-level advisory lock. Unlike the
/// SQLite gate there is no process-local turn, and no cross-process exclusion beyond what the
/// lock itself provides. Nothing spans the preparation and the migration transactionally: a
/// created database or a committed migration is not undone by a later failure or cancellation.
/// </para>
/// <para>
/// The installation row is read once, after the orchestration succeeded and inside the same
/// migration scope. A missing or invalid row is a finite failure left to human disposition; this
/// gate never creates, repairs, or completes one, and it never issues a Setup Code.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlStartupCoordinator
{
    private readonly IServiceProvider services;
    private readonly ReferencePostgreSqlStartupOptions options;
    private readonly ILogger<ReferencePostgreSqlStartupCoordinator> logger;

    /// <summary>Creates the coordinator over the host's own container.</summary>
    public ReferencePostgreSqlStartupCoordinator(
        IServiceProvider services,
        ReferencePostgreSqlStartupOptions options,
        ILogger<ReferencePostgreSqlStartupCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.services = services;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Runs the gate once and reports a finite outcome.</summary>
    /// <remarks>
    /// <para>
    /// A finite result is published and recorded only after this call's own migration scope has
    /// been released and the caller's token has been read one last time. A caller cancellation
    /// observed at that checkpoint outranks the outcome that was already computed and is reported
    /// with the caller's own token, and a release that fails leaves no Ready result and no success
    /// record behind - the outcome falls back to a migration failure.
    /// </para>
    /// <para>
    /// The window after that checkpoint is not covered, a hung release is not forcibly
    /// terminated, and nothing here undoes a created database or a committed migration.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">The caller's token. It propagates unchanged.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled the startup.</exception>
    public async ValueTask<ReferencePostgreSqlStartupResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            serverVersion: null,
            options.TargetConnectionString);

        var preparation = services.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        if (!preparation.TryGetProvider(target.Provider, out var provider) || provider is null)
        {
            return Report(ReferencePostgreSqlStartupOutcome.TargetUnavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observed = await ObserveAsync(provider, target, cancellationToken).ConfigureAwait(false);
        if (observed is not null)
        {
            return observed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await MigrateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ReferencePostgreSqlStartupResult?> ObserveAsync(
        IDatabaseTargetPreparationProvider provider,
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        DatabaseTargetObservation observation;
        try
        {
            observation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return Report(ReferencePostgreSqlStartupOutcome.TargetUnavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Status == DatabaseTargetObservationStatus.TargetConnectable)
        {
            return null;
        }

        if (observation.Status != DatabaseTargetObservationStatus.TargetMissing)
        {
            // An unreachable server, an unusable target, an authentication or permission failure:
            // a target that is present but cannot be used is never adopted, fixed, or replaced.
            return Report(ReferencePostgreSqlStartupOutcome.TargetUnavailable);
        }

        if (!options.PrepareIfMissing)
        {
            return Report(ReferencePostgreSqlStartupOutcome.TargetMissing);
        }

        var administrative = options.AdministrativeConnectionString;
        if (administrative is null)
        {
            // The pairing is fixed when the inputs are read; an unpaired request fails closed
            // instead of preparing with credentials this call does not hold.
            return Report(ReferencePostgreSqlStartupOutcome.PreparationFailed);
        }

        DatabaseTargetPreparationResult result;
        try
        {
            result = await provider.PrepareAsync(
                new DatabaseTargetPreparationRequest(target, administrative),
                ReferencePostgreSqlStartupOptions.PreparationBudget,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return Report(ReferencePostgreSqlStartupOutcome.PreparationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded ? null : Report(ReferencePostgreSqlStartupOutcome.PreparationFailed);
    }

    private async ValueTask<ReferencePostgreSqlStartupResult> MigrateAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        // One scope for this call: the consumer's own context, installation store, and executor
        // all resolve from it, and the installation row is read from that same scope.
        var scope = services.CreateAsyncScope();
        ReferencePostgreSqlStartupOutcome outcome;
        ServiceStartupPhase? phase = null;
        bool executorWasCalled;
        try
        {
            var orchestrator = new DatabaseMigrationOrchestrator(
                scope.ServiceProvider.GetRequiredService<IDatabaseMigrationExecutor>(),
                services.GetRequiredService<DatabaseMigrationLockProviderRegistry>());

            var result = await orchestrator.OrchestrateMigrationAsync(
                services.GetRequiredService<ServiceId>(),
                target,
                ReferencePostgreSqlStartupOptions.LockAcquireBudget,
                cancellationToken).ConfigureAwait(false);

            // The orchestrator's own message is never projected; only the finite outcome is.
            if (result.Succeeded)
            {
                executorWasCalled = result.ExecutorWasCalled;
                var (readOutcome, readPhase) = await ReadInstallationAsync(scope, cancellationToken)
                    .ConfigureAwait(false);
                outcome = readOutcome;
                phase = readPhase;
            }
            else
            {
                executorWasCalled = result.ExecutorWasCalled;
                outcome = MapFailure(result);
            }
        }
        catch (Exception)
        {
            // The work itself failed. This call's scope is still released before that failure
            // leaves the coordinator, and nothing is published or logged for this call.
            await ReleaseAfterFailureAsync(scope).ConfigureAwait(false);
            throw;
        }

        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's cancellation outranks the release failure. The checkpoint below reports
            // it with the caller's own token.
        }
        catch (Exception)
        {
            // A release this call owns did not finish, so this call has nothing it may publish as
            // Ready, whatever the orchestration and the installation read already established.
            // The exception's own text and inner exception are never projected.
            outcome = ReferencePostgreSqlStartupOutcome.MigrationFailed;
            phase = null;
        }

        // The single finalisation checkpoint: the work is over, this call's scope is released,
        // and only then may a finite result be published and recorded.
        cancellationToken.ThrowIfCancellationRequested();
        return Report(outcome, executorWasCalled, phase);
    }

    private async ValueTask<(ReferencePostgreSqlStartupOutcome Outcome, ServiceStartupPhase? Phase)>
        ReadInstallationAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        ServiceInstallationState? state;
        try
        {
            state = await scope.ServiceProvider.GetRequiredService<IServiceInstallationStore>()
                .FindAsync(services.GetRequiredService<ServiceId>(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ServiceInstallationStoreException)
        {
            // An invalid entity, a violated state invariant, or an unreadable store - including a
            // non-caller cancellation inside the store - is a finite failure for human
            // disposition. The gate never creates or repairs the row.
            return (ReferencePostgreSqlStartupOutcome.InstallationStateInvalid, null);
        }

        if (state is null)
        {
            // An old workspace-only database or a deleted row: never adopted or repaired here.
            return (ReferencePostgreSqlStartupOutcome.InstallationStateMissing, null);
        }

        // The gate's existence proves the bootstrap configuration, so the phase is decided by the
        // installation row alone. The row is the startup-time fact, not live health.
        return (
            ReferencePostgreSqlStartupOutcome.Ready,
            ServiceStartupPhaseResolver.Resolve(hasBootstrapConfiguration: true, state));
    }

    private static ReferencePostgreSqlStartupOutcome MapFailure(MigrationExecutionResult result) =>
        result.ErrorCode switch
        {
            WellKnownMigrationErrorCodes.LockTimeout or
            WellKnownMigrationErrorCodes.LockFailed or
            WellKnownMigrationErrorCodes.LockNotSupported
                => ReferencePostgreSqlStartupOutcome.LockUnavailable,
            WellKnownMigrationErrorCodes.VersionTooNew
                => ReferencePostgreSqlStartupOutcome.VersionTooNew,
            // inspection_failed, execution_failed, final_state_invalid, and anything not
            // recognized above fail closed as a migration failure.
            _ => ReferencePostgreSqlStartupOutcome.MigrationFailed,
        };

    private static async ValueTask ReleaseAfterFailureAsync(AsyncServiceScope scope)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The failure already on its way to the caller is the one this call reports; a release
            // that fails on top of it adds no finite outcome of its own.
        }
    }

    private ReferencePostgreSqlStartupResult Report(
        ReferencePostgreSqlStartupOutcome outcome,
        bool executorWasCalled = false,
        ServiceStartupPhase? phase = null)
    {
        // The one line this sample writes carries the finite outcome and nothing else: no
        // connection string, no password, no provider or orchestrator message, no exception text.
        logger.LogInformation(
            "Reference PostgreSQL startup deployment finished with outcome {Outcome}.",
            outcome);
        return new ReferencePostgreSqlStartupResult(outcome, executorWasCalled, phase);
    }
}

/// <summary>
/// Completes the PostgreSQL startup deployment before any hosted service, including the web host,
/// starts, so the host never accepts a request over an unmigrated or unregistered database.
/// </summary>
public sealed class ReferencePostgreSqlStartupHostedService : IHostedLifecycleService
{
    private readonly ReferencePostgreSqlStartupCoordinator coordinator;

    /// <summary>Creates the startup step.</summary>
    public ReferencePostgreSqlStartupHostedService(ReferencePostgreSqlStartupCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        this.coordinator = coordinator;
    }

    /// <summary>Gets the last outcome, or null before the gate ran.</summary>
    public ReferencePostgreSqlStartupResult? Result { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// The gate's result is stored only once the coordinator has published one, so a startup that
    /// was cancelled at the coordinator's finalisation checkpoint leaves no result behind and stops
    /// this host from starting.
    /// </remarks>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await coordinator.RunAsync(cancellationToken).ConfigureAwait(false);
        Result = result;
        if (!result.IsReady)
        {
            // The message carries the finite outcome only.
            throw new InvalidOperationException(
                $"The reference PostgreSQL startup deployment did not complete: {result.Outcome}.");
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

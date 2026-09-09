using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.ReferenceService.Database.Sqlite;

/// <summary>
/// Runs the sample's opt-in SQLite startup deployment gate: authorize the mode, observe the target,
/// prepare it only when that was explicitly permitted, and migrate through the consumer's own scope.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed and fails closed. The deployment mode is validated first, from the captured
/// capability declarations alone, so an unauthorized mode never reaches an observation, a
/// preparation, a target identity, or EF. A missing target is not created unless the consumer said
/// so, and a target that is present but unusable is never repaired.
/// </para>
/// <para>
/// This is a consumer example. It provides no cross-process or cross-host exclusion - two processes
/// that both declare SingleInstance are a deployment error this contract cannot detect - and no
/// transaction spans the file publication and the migration: a committed migration or a published
/// empty file is not undone by a later cancellation.
/// </para>
/// </remarks>
public sealed class ReferenceSqliteStartupCoordinator
{
    private readonly IServiceProvider services;
    private readonly ReferenceSqliteStartupOptions options;
    private readonly ILogger<ReferenceSqliteStartupCoordinator> logger;

    /// <summary>Creates the coordinator over the host's own container.</summary>
    public ReferenceSqliteStartupCoordinator(
        IServiceProvider services,
        ReferenceSqliteStartupOptions options,
        ILogger<ReferenceSqliteStartupCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.services = services;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Runs the gate once and reports a finite outcome.</summary>
    /// <param name="cancellationToken">The caller's token. It propagates unchanged.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled the startup.</exception>
    public async ValueTask<ReferenceSqliteStartupResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deploymentCapabilities = services.GetRequiredService<DatabaseDeploymentCapabilityRegistry>();
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.Sqlite,
            serverVersion: null,
            options.TargetConnectionString);

        // The mode decision comes before every side effect, from declarations only.
        if (!new DatabaseDeploymentValidator(deploymentCapabilities)
                .Validate(target.Provider, options.DeploymentMode)
                .IsSupported)
        {
            return Report(ReferenceSqliteStartupOutcome.DeploymentModeRejected);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preparation = services.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        if (!preparation.TryGetProvider(target.Provider, out var provider) || provider is null)
        {
            return Report(ReferenceSqliteStartupOutcome.TargetUnavailable);
        }

        var observed = await ObserveAsync(provider, target, cancellationToken).ConfigureAwait(false);
        if (observed is not null)
        {
            return observed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await MigrateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ReferenceSqliteStartupResult?> ObserveAsync(
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
            return Report(ReferenceSqliteStartupOutcome.TargetUnavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Status == DatabaseTargetObservationStatus.TargetConnectable)
        {
            return null;
        }

        if (observation.Status != DatabaseTargetObservationStatus.TargetMissing)
        {
            // A target that exists but cannot be used is never adopted, fixed, or replaced.
            return Report(ReferenceSqliteStartupOutcome.TargetUnavailable);
        }

        if (!options.PrepareIfMissing)
        {
            return Report(ReferenceSqliteStartupOutcome.TargetMissing);
        }

        DatabaseTargetPreparationResult result;
        try
        {
            result = await provider.PrepareAsync(
                DatabaseTargetPreparationRequest.ForFile(target),
                ReferenceSqliteStartupOptions.Budget,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            return Report(ReferenceSqliteStartupOutcome.PreparationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded ? null : Report(ReferenceSqliteStartupOutcome.PreparationFailed);
    }

    private async ValueTask<ReferenceSqliteStartupResult> MigrateAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        // One scope for this call, with the consumer's own context and executor inside it.
        await using var scope = services.CreateAsyncScope();
        var orchestrator = new DatabaseMigrationOrchestrator(
            scope.ServiceProvider.GetRequiredService<IDatabaseMigrationExecutor>(),
            services.GetRequiredService<DatabaseMigrationLockProviderRegistry>(),
            services.GetRequiredService<DatabaseDeploymentCapabilityRegistry>());

        var result = await orchestrator.OrchestrateMigrationAsync(
            services.GetRequiredService<ServiceId>(),
            target,
            options.DeploymentMode,
            ReferenceSqliteStartupOptions.Budget,
            cancellationToken).ConfigureAwait(false);

        // The orchestrator's own message is never projected; only the finite outcome is.
        return Report(
            result.Succeeded
                ? ReferenceSqliteStartupOutcome.Ready
                : ReferenceSqliteStartupOutcome.MigrationFailed,
            result.ExecutorWasCalled);
    }

    private ReferenceSqliteStartupResult Report(
        ReferenceSqliteStartupOutcome outcome,
        bool executorWasCalled = false)
    {
        // The one line this sample writes carries the finite outcome and nothing else: no path, no
        // connection string, no provider or orchestrator message, and no exception text.
        logger.LogInformation(
            "Reference SQLite startup deployment finished with outcome {Outcome}.",
            outcome);
        return new ReferenceSqliteStartupResult(outcome, executorWasCalled);
    }
}

/// <summary>
/// Completes the SQLite startup deployment before any hosted service, including the web host,
/// starts, so the host never accepts a request over an unmigrated database.
/// </summary>
public sealed class ReferenceSqliteStartupHostedService : IHostedLifecycleService
{
    private readonly ReferenceSqliteStartupCoordinator coordinator;

    /// <summary>Creates the startup step.</summary>
    public ReferenceSqliteStartupHostedService(ReferenceSqliteStartupCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        this.coordinator = coordinator;
    }

    /// <summary>Gets the last outcome, or null before the gate ran.</summary>
    public ReferenceSqliteStartupResult? Result { get; private set; }

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await coordinator.RunAsync(cancellationToken).ConfigureAwait(false);
        Result = result;
        if (!result.IsReady)
        {
            // The message carries the finite outcome only.
            throw new InvalidOperationException(
                $"The reference SQLite startup deployment did not complete: {result.Outcome}.");
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

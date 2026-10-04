using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;

namespace ServiceMantle.Migration;

/// <summary>
/// Runs the fixed startup database sequence in one call: deployment validation, optional target
/// preparation, migration orchestration, and receipt recording. This is the shared implementation
/// behind both the host-lifetime entry and any direct, caller-driven invocation.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed and fails closed. Deployment validation reads captured declarations only
/// and runs before any I/O. Target preparation is opt-in: without it no
/// <see cref="IDatabaseTargetPreparationProvider"/> is called at all; with it, an existing target
/// is only observed, a missing target is created only when that was explicitly permitted, and the
/// target must be observed connectable again after a preparation before migration starts.
/// Unreachable servers, authentication, permission, and identity failures are refused; they are
/// never interpreted as a missing target.
/// </para>
/// <para>
/// Any stage failure fails the gate: the receipt records <see cref="ServiceMigrationReadinessState.Failed"/>
/// with a safe error code from the ServiceMantle allow lists before the failure is returned or
/// thrown. Caller cancellation ends the gate with
/// <see cref="OperationCanceledException"/> without entering later stages and without recording
/// success; the receipt then remains in the running state. Nothing spans the preparation and the
/// migration transactionally: a created target or a committed migration is not undone by a later
/// failure or cancellation.
/// </para>
/// <para>
/// The gate runs at most once per <see cref="StartupDatabaseReceipt"/> instance: a repeated
/// invocation throws instead of re-running stages against a receipt that already holds a state.
/// </para>
/// </remarks>
public sealed class StartupDatabaseGate
{
    private readonly DatabaseDeploymentCapabilityRegistry deploymentCapabilities;
    private readonly DatabaseTargetPreparationProviderRegistry preparationProviders;
    private readonly DatabaseMigrationLockProviderRegistry lockProviders;
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>Initializes the gate over the shared registries and the migration scope factory.</summary>
    /// <param name="deploymentCapabilities">The captured deployment capability declarations.</param>
    /// <param name="preparationProviders">The registered target preparation providers.</param>
    /// <param name="lockProviders">The registered migration lock providers.</param>
    /// <param name="scopeFactory">
    /// The scope factory the gate uses to resolve the consuming service's scoped
    /// <see cref="IDatabaseMigrationExecutor"/> for the migration stage.
    /// </param>
    public StartupDatabaseGate(
        DatabaseDeploymentCapabilityRegistry deploymentCapabilities,
        DatabaseTargetPreparationProviderRegistry preparationProviders,
        DatabaseMigrationLockProviderRegistry lockProviders,
        IServiceScopeFactory scopeFactory)
    {
        this.deploymentCapabilities = deploymentCapabilities ??
            throw new ArgumentNullException(nameof(deploymentCapabilities));
        this.preparationProviders = preparationProviders ??
            throw new ArgumentNullException(nameof(preparationProviders));
        this.lockProviders = lockProviders ?? throw new ArgumentNullException(nameof(lockProviders));
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>Runs the startup database sequence once and records the receipt.</summary>
    /// <param name="options">The immutable gate inputs.</param>
    /// <param name="receipt">The receipt this run records.</param>
    /// <param name="serviceId">The identity shared by all instances of the service.</param>
    /// <param name="cancellationToken">
    /// The caller's token. It propagates unchanged through every stage.</param>
    /// <returns>The finite gate outcome with a safe error code on failure.</returns>
    /// <exception cref="InvalidOperationException">
    /// The receipt already started or completed, or no migration executor is registered.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the startup.</exception>
    public async ValueTask<StartupDatabaseGateResult> RunAsync(
        StartupDatabaseGateOptions options,
        StartupDatabaseReceipt receipt,
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(serviceId);

        if (!receipt.TryMarkRunning())
        {
            throw new InvalidOperationException(
                "The startup database gate has already started or completed for this receipt.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var preparation = await PrepareCoreAsync(options, cancellationToken).ConfigureAwait(false);
        if (!preparation.Succeeded)
        {
            return Complete(receipt, StartupDatabaseGateResult.Failure(preparation.ErrorCode!));
        }

        // 3. Migration orchestration.
        return await OrchestrateAsync(options, receipt, serviceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates deployment and optionally prepares the target without migration or receipt changes.</summary>
    /// <param name="options">Explicit immutable inputs. Disabled preparation still validates deployment.</param>
    /// <param name="cancellationToken">The caller token, checked after provider completion and cleanup.</param>
    /// <returns>A finite result; skipped success does not prove target connectivity.</returns>
    /// <remarks>Creates no migration scope and resolves no executor. No state is cached between calls.
    /// A later RunAsync must explicitly disable preparation to skip it; changes between calls and
    /// already committed creation are not protected or rolled back.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled preparation.</exception>
    public ValueTask<StartupDatabasePreparationResult> PrepareAsync(
        StartupDatabaseGateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PrepareCoreAsync(options, cancellationToken);
    }

    private async ValueTask<StartupDatabasePreparationResult> PrepareCoreAsync(
        StartupDatabaseGateOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = new DatabaseDeploymentValidator(deploymentCapabilities)
            .Validate(options.Database.Provider, options.DeploymentMode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsSupported)
        {
            return StartupDatabasePreparationResult.Failure(
                validation.MigrationErrorCode ?? WellKnownMigrationErrorCodes.LockNotSupported);
        }
        if (!options.EnableTargetPreparation)
        {
            return StartupDatabasePreparationResult.Success(skipped: true);
        }
        var failure = await PrepareTargetAsync(options, cancellationToken).ConfigureAwait(false);
        // One shared exit checkpoint covers every normal failure/success branch after cleanup.
        cancellationToken.ThrowIfCancellationRequested();
        return failure ?? StartupDatabasePreparationResult.Success(skipped: false);
    }

    private async ValueTask<StartupDatabasePreparationResult?> PrepareTargetAsync(
        StartupDatabaseGateOptions options,
        CancellationToken cancellationToken)
    {
        if (!preparationProviders.TryGetProvider(options.Database.Provider, out var provider) ||
            provider is null)
        {
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);
        }

        var observation = await ObserveTargetAsync(provider, options.Database, cancellationToken)
            .ConfigureAwait(false);
        if (observation is null)
        {
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }

        if (observation.Status == DatabaseTargetObservationStatus.TargetConnectable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (observation.Status != DatabaseTargetObservationStatus.TargetMissing)
        {
            // An unreachable server, an unusable target, an authentication or permission failure
            // is refused; a target that is present but unusable is never adopted, repaired, or
            // replaced, and never interpreted as missing.
            return StartupDatabasePreparationResult.Failure(
                observation.ErrorCode ??
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preparation = await PrepareMissingTargetAsync(provider, options, cancellationToken)
            .ConfigureAwait(false);
        if (preparation is not null)
        {
            return preparation;
        }

        // A prepared target must be observed connectable again before migration starts.
        var reObservation = await ObserveTargetAsync(provider, options.Database, cancellationToken)
            .ConfigureAwait(false);
        if (reObservation is null)
        {
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.NotConnectableAfterPreparation);
        }

        return reObservation.Status == DatabaseTargetObservationStatus.TargetConnectable
            ? null
            : StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.NotConnectableAfterPreparation);
    }

    private static async ValueTask<DatabaseTargetObservation?> ObserveTargetAsync(
        IDatabaseTargetPreparationProvider provider,
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        try
        {
            var observation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return observation;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreateCallerCancellation(cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async ValueTask<StartupDatabasePreparationResult?> PrepareMissingTargetAsync(
        IDatabaseTargetPreparationProvider provider,
        StartupDatabaseGateOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.AllowTargetCreation)
        {
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.CreationNotAllowed);
        }

        var maintenanceConnectionString = options.MaintenanceConnectionString;
        if (maintenanceConnectionString is null &&
            provider.TargetKind == BootstrapDatabaseTargetKind.ServerDatabase)
        {
            // A server target cannot be created without an administrative connection; a file
            // target needs none, so the request stays a file request there.
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        var request = maintenanceConnectionString is null
            ? DatabaseTargetPreparationRequest.ForFile(options.Database)
            : new DatabaseTargetPreparationRequest(options.Database, maintenanceConnectionString);

        DatabaseTargetPreparationResult preparation;
        try
        {
            preparation = await provider.PrepareAsync(
                request,
                options.PreparationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreateCallerCancellation(cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StartupDatabasePreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return preparation.Succeeded
            ? null
            : StartupDatabasePreparationResult.Failure(
                preparation.ErrorCode ??
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
    }

    private async ValueTask<StartupDatabaseGateResult> OrchestrateAsync(
        StartupDatabaseGateOptions options,
        StartupDatabaseReceipt receipt,
        ServiceId serviceId,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var executor = scope.ServiceProvider.GetService<IDatabaseMigrationExecutor>();
            if (executor is null)
            {
                receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed);
                throw new InvalidOperationException(
                    "No ServiceMantle database migration executor is registered for the startup database gate.");
            }

            var orchestrator = new DatabaseMigrationOrchestrator(
                executor,
                lockProviders,
                deploymentCapabilities);

            MigrationExecutionResult result;
            try
            {
                result = await orchestrator.OrchestrateMigrationAsync(
                    serviceId,
                    options.Database,
                    options.DeploymentMode,
                    options.LockWaitBudget,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw CreateCallerCancellation(cancellationToken);
            }

            return result.Succeeded
                ? Complete(receipt, StartupDatabaseGateResult.Success(result.ExecutorWasCalled))
                : Complete(receipt, StartupDatabaseGateResult.Failure(
                    result.ErrorCode ?? WellKnownMigrationErrorCodes.ExecutionFailed,
                    result.ExecutorWasCalled));
        }
        finally
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // The caller's cancellation outranks a scope release failure; the checkpoint at
                // the caller reports cancellation with the caller's own token.
            }
            catch (Exception)
            {
                // A scope this call owns did not finish releasing; the stage result already
                // recorded on the receipt stands and the release failure is not projected.
            }
        }
    }

    private static StartupDatabaseGateResult Complete(
        StartupDatabaseReceipt receipt,
        StartupDatabaseGateResult result)
    {
        if (result.Succeeded)
        {
            receipt.TryCompleteSucceeded();
        }
        else
        {
            receipt.TryCompleteFailed(result.ErrorCode!);
        }

        return result;
    }

    private static OperationCanceledException CreateCallerCancellation(CancellationToken cancellationToken) =>
        new("The startup database gate was cancelled by the caller.", cancellationToken);
}

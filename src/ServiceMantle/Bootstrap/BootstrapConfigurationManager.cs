using ServiceMantle;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// Orchestrates safe Bootstrap status, creation, and replacement use cases.
/// </summary>
public sealed class BootstrapConfigurationManager
{
    private readonly BootstrapFileStore fileStore;
    private readonly InstanceId instanceId;
    private readonly BootstrapDatabaseProviderRegistry providerRegistry;
    private readonly IBootstrapCandidateValidator candidateValidator;
    private readonly SemaphoreSlim modificationGate = new(1, 1);

    /// <summary>
    /// Initializes a manager for one instance-local Bootstrap file.
    /// </summary>
    /// <param name="fileStore">The store for the current service.</param>
    /// <param name="instanceId">The instance performing management operations.</param>
    /// <param name="providerRegistry">The registry used to canonicalize provider ids before persistence.</param>
    /// <param name="candidateValidator">The validator called before every write.</param>
    public BootstrapConfigurationManager(
        BootstrapFileStore fileStore,
        InstanceId instanceId,
        BootstrapDatabaseProviderRegistry providerRegistry,
        IBootstrapCandidateValidator candidateValidator)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(providerRegistry);
        ArgumentNullException.ThrowIfNull(candidateValidator);

        this.fileStore = fileStore;
        this.instanceId = instanceId;
        this.providerRegistry = providerRegistry;
        this.candidateValidator = candidateValidator;
    }

    /// <summary>
    /// Gets the current safe Bootstrap status.
    /// </summary>
    /// <returns>A safe status projection that never contains secret values.</returns>
    /// <exception cref="BootstrapException">The existing file is damaged or mismatched.</exception>
    public BootstrapManagementStatus GetStatus()
    {
        var configuration = fileStore.TryLoad();
        if (configuration is null)
        {
            return new BootstrapManagementStatus(
                fileStore.ServiceId,
                instanceId,
                isConfigured: false,
                provider: null,
                serverVersion: null,
                connectionStringConfigured: false,
                masterKeyConfigured: false);
        }

        return new BootstrapManagementStatus(
            configuration.ServiceId,
            instanceId,
            isConfigured: true,
            provider: configuration.Database.Provider,
            serverVersion: configuration.Database.ServerVersion,
            connectionStringConfigured: true,
            masterKeyConfigured: true);
    }

    /// <summary>
    /// Validates and creates the instance-local Bootstrap file without overwriting it.
    /// </summary>
    /// <param name="request">The complete Bootstrap creation request.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>A safe result describing the completed creation.</returns>
    /// <exception cref="BootstrapException">The target file cannot be created.</exception>
    /// <exception cref="BootstrapManagementException">Candidate validation failed.</exception>
    public async ValueTask<BootstrapChangeResult> CreateAsync(
        BootstrapCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await modificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new BootstrapConfiguration(
                fileStore.ServiceId,
                request.Database,
                request.MasterKey);

            await ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            fileStore.Create(CanonicalizeValidatedCandidate(candidate));

            return new BootstrapChangeResult(
                fileStore.ServiceId,
                instanceId,
                BootstrapChangeOperation.Create);
        }
        finally
        {
            modificationGate.Release();
        }
    }

    /// <summary>
    /// Validates and atomically replaces selected parts of an existing Bootstrap file.
    /// </summary>
    /// <param name="request">The replacement request.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>A safe result describing the completed update.</returns>
    /// <exception cref="BootstrapException">The existing file cannot be loaded or replaced.</exception>
    /// <exception cref="BootstrapManagementException">The update is empty or candidate validation failed.</exception>
    public async ValueTask<BootstrapChangeResult> UpdateAsync(
        BootstrapUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ReplacementDatabase is null &&
            string.IsNullOrWhiteSpace(request.ReplacementMasterKey))
        {
            throw new BootstrapManagementException(
                "update.empty",
                "Bootstrap update must provide a database or master-key replacement.");
        }

        await modificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = fileStore.Load();
            var database = request.ReplacementDatabase ?? existing.Database;
            var masterKey = string.IsNullOrWhiteSpace(request.ReplacementMasterKey)
                ? existing.MasterKey
                : request.ReplacementMasterKey;

            var candidate = new BootstrapConfiguration(
                fileStore.ServiceId,
                database,
                masterKey);

            await ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            fileStore.Replace(CanonicalizeValidatedCandidate(candidate));

            return new BootstrapChangeResult(
                fileStore.ServiceId,
                instanceId,
                BootstrapChangeOperation.Update);
        }
        finally
        {
            modificationGate.Release();
        }
    }

    private async ValueTask ValidateCandidateAsync(
        BootstrapConfiguration candidate,
        CancellationToken cancellationToken)
    {
        BootstrapValidationResult? validationResult;
        try
        {
            validationResult = await candidateValidator
                .ValidateAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BootstrapManagementException(
                "candidate.validation_failed",
                "Bootstrap candidate validation failed.");
        }

        if (validationResult is null)
        {
            throw new BootstrapManagementException(
                "candidate.invalid_result",
                "Bootstrap candidate validation failed.");
        }

        if (!validationResult.IsValid)
        {
            throw new BootstrapManagementException(
                validationResult.ErrorCode ?? "candidate.rejected",
                "Bootstrap candidate validation failed.");
        }
    }

    private BootstrapConfiguration CanonicalizeValidatedCandidate(
        BootstrapConfiguration candidate)
    {
        if (!providerRegistry.TryGetCanonicalProviderId(
                candidate.Database.Provider,
                out var canonicalProviderId) ||
            canonicalProviderId is null)
        {
            throw new BootstrapManagementException(
                "database.provider_not_registered",
                "Bootstrap candidate validation failed.");
        }

        if (string.Equals(
            candidate.Database.Provider,
            canonicalProviderId,
            StringComparison.Ordinal))
        {
            return candidate;
        }

        return new BootstrapConfiguration(
            candidate.ServiceId,
            new BootstrapDatabaseConfiguration(
                canonicalProviderId,
                candidate.Database.ServerVersion,
                candidate.Database.ConnectionString),
            candidate.MasterKey);
    }
}

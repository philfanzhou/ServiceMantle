namespace ServiceMantle.Bootstrap;

/// <summary>
/// Resolves and holds a read-only database target preparation provider registration table.
/// Provider IDs are matched case-insensitively. When constructed with a
/// <see cref="BootstrapDatabaseProviderRegistry"/>, aliases declared there resolve to the
/// preparation provider registered under the corresponding canonical ID.
/// </summary>
/// <remarks>
/// This registry is deliberately separate from <see cref="BootstrapDatabaseProviderRegistry"/> so
/// that registering a bootstrap validation provider never implies the optional target preparation
/// capability is supported. Callers must use <see cref="TryGetProvider"/> and fail closed with
/// <see cref="WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported"/> when no
/// preparation provider is registered for a database provider id.
/// </remarks>
public sealed class DatabaseTargetPreparationProviderRegistry
{
    private readonly Dictionary<string, IDatabaseTargetPreparationProvider> registrations;
    private readonly BootstrapDatabaseProviderRegistry? bootstrapProviderRegistry;

    /// <summary>
    /// Initializes a database target preparation provider registry.
    /// </summary>
    /// <param name="providers">All preparation providers to register. Null or empty enumerable is allowed.</param>
    /// <exception cref="ArgumentException">A provider is null or a provider ID is already registered.</exception>
    public DatabaseTargetPreparationProviderRegistry(
        IEnumerable<IDatabaseTargetPreparationProvider>? providers = null)
    {
        registrations = new Dictionary<string, IDatabaseTargetPreparationProvider>(StringComparer.OrdinalIgnoreCase);

        if (providers is null)
        {
            return;
        }

        foreach (var provider in providers)
        {
            if (provider is null)
            {
                throw new ArgumentNullException(nameof(providers), "Provider cannot be null.");
            }

            var providerId = DatabaseProviderId.Normalize(provider.ProviderId, nameof(providers));

            if (!Enum.IsDefined(provider.TargetKind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(providers),
                    provider.TargetKind,
                    "The database target preparation provider has an undefined target kind.");
            }

            if (registrations.ContainsKey(providerId))
            {
                throw new ArgumentException(
                    $"A database target preparation provider with ID '{providerId}' is already registered.",
                    nameof(providers));
            }

            registrations.Add(providerId, provider);
        }
    }

    /// <summary>
    /// Initializes a database target preparation provider registry that also resolves aliases
    /// declared by the bootstrap provider registry.
    /// </summary>
    /// <param name="providers">All preparation providers to register. Null or empty enumerable is allowed.</param>
    /// <param name="bootstrapProviderRegistry">
    /// Bootstrap provider metadata used only to map aliases to canonical provider IDs. A bootstrap
    /// provider still has no preparation capability unless its canonical ID is present in
    /// <paramref name="providers"/>.
    /// </param>
    public DatabaseTargetPreparationProviderRegistry(
        IEnumerable<IDatabaseTargetPreparationProvider>? providers,
        BootstrapDatabaseProviderRegistry bootstrapProviderRegistry)
        : this(providers)
    {
        ArgumentNullException.ThrowIfNull(bootstrapProviderRegistry);
        this.bootstrapProviderRegistry = bootstrapProviderRegistry;
    }

    /// <summary>
    /// Attempts to resolve a preparation provider by database provider ID (case-insensitive).
    /// </summary>
    /// <param name="providerId">The canonical database provider ID or a configured alias.</param>
    /// <param name="provider">The preparation provider when found.</param>
    /// <returns>true when a preparation provider is registered for the ID; otherwise, false.</returns>
    public bool TryGetProvider(string? providerId, out IDatabaseTargetPreparationProvider? provider)
    {
        provider = null;

        if (!DatabaseProviderId.TryNormalize(providerId, out var normalizedProviderId))
        {
            return false;
        }

        if (registrations.TryGetValue(normalizedProviderId, out provider))
        {
            return true;
        }

        if (bootstrapProviderRegistry is null ||
            !bootstrapProviderRegistry.TryGetProvider(normalizedProviderId, out var bootstrapProvider) ||
            bootstrapProvider is null)
        {
            return false;
        }

        if (!registrations.TryGetValue(bootstrapProvider.Descriptor.Id, out var registeredProvider))
        {
            return false;
        }

        provider = new CanonicalizingProvider(
            registeredProvider,
            bootstrapProvider.Descriptor);
        return true;
    }

    private sealed class CanonicalizingProvider : IDatabaseTargetPreparationProvider
    {
        private readonly IDatabaseTargetPreparationProvider provider;
        private readonly BootstrapDatabaseProviderDescriptor descriptor;

        public CanonicalizingProvider(
            IDatabaseTargetPreparationProvider provider,
            BootstrapDatabaseProviderDescriptor descriptor)
        {
            this.provider = provider;
            this.descriptor = descriptor;
        }

        public string ProviderId => provider.ProviderId;

        public BootstrapDatabaseTargetKind TargetKind => provider.TargetKind;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            provider.ObserveAsync(Canonicalize(target), cancellationToken);

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var canonicalTarget = Canonicalize(request.Target);
            if (ReferenceEquals(canonicalTarget, request.Target))
            {
                return provider.PrepareAsync(request, timeout, cancellationToken);
            }

            return provider.PrepareAsync(
                new DatabaseTargetPreparationRequest(
                    canonicalTarget,
                    request.AdministrativeConnectionString),
                timeout,
                cancellationToken);
        }

        private BootstrapDatabaseConfiguration Canonicalize(BootstrapDatabaseConfiguration target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (string.Equals(target.Provider, descriptor.Id, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            var isDeclaredAlias = descriptor.Aliases.Any(alias =>
                string.Equals(target.Provider, alias, StringComparison.OrdinalIgnoreCase));
            if (!isDeclaredAlias)
            {
                return target;
            }

            return new BootstrapDatabaseConfiguration(
                descriptor.Id,
                target.ServerVersion,
                target.ConnectionString);
        }
    }
}

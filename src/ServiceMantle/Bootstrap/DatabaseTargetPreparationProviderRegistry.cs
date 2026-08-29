namespace ServiceMantle.Bootstrap;

/// <summary>
/// Resolves and holds a read-only database target preparation provider registration table.
/// Provider IDs are matched case-insensitively.
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
    private readonly DatabaseProviderIdResolver providerIdResolver;

    /// <summary>
    /// Initializes a database target preparation provider registry.
    /// </summary>
    /// <param name="providers">All preparation providers to register. Null or empty enumerable is allowed.</param>
    /// <param name="providerIdResolver">
    /// The shared provider-id resolver snapshot, or null to use <see cref="DatabaseProviderIdResolver.Empty"/>.
    /// Registration keys and lookup keys are canonicalized through it, so a bootstrap alias and the
    /// canonical id select the same registration. Resolving an alias never implies that a
    /// preparation provider exists for it: an unregistered capability still fails closed.
    /// </param>
    /// <exception cref="ArgumentException">A provider is null or a provider ID is already registered.</exception>
    public DatabaseTargetPreparationProviderRegistry(
        IEnumerable<IDatabaseTargetPreparationProvider>? providers = null,
        DatabaseProviderIdResolver? providerIdResolver = null)
    {
        registrations = new Dictionary<string, IDatabaseTargetPreparationProvider>(StringComparer.OrdinalIgnoreCase);
        this.providerIdResolver = providerIdResolver ?? DatabaseProviderIdResolver.Empty;

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

            var declaredProviderId = DatabaseProviderId.Normalize(provider.ProviderId, nameof(providers));
            var providerId = this.providerIdResolver.Canonicalize(declaredProviderId);

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
    /// Attempts to resolve a preparation provider by database provider ID or registered alias
    /// (case-insensitive).
    /// </summary>
    /// <param name="providerId">The database provider ID or registered alias.</param>
    /// <param name="provider">The preparation provider when found.</param>
    /// <returns>true when a preparation provider is registered for the ID; otherwise, false.</returns>
    public bool TryGetProvider(string? providerId, out IDatabaseTargetPreparationProvider? provider)
    {
        provider = null;

        if (!providerIdResolver.TryCanonicalize(providerId, out var canonicalProviderId))
        {
            return false;
        }

        return registrations.TryGetValue(canonicalProviderId, out provider);
    }
}

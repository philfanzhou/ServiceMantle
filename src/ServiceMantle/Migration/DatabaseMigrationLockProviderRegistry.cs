using ServiceMantle.Bootstrap;

namespace ServiceMantle.Migration;

/// <summary>
/// Resolves and holds a read-only migration lock provider registration table.
/// Provider IDs are matched case-insensitively.
/// </summary>
public sealed class DatabaseMigrationLockProviderRegistry
{
    private readonly Dictionary<string, IDatabaseMigrationLockProvider> registrations;
    private readonly DatabaseProviderIdResolver providerIdResolver;

    /// <summary>
    /// Initializes a lock provider registry.
    /// </summary>
    /// <param name="providers">All lock providers to register. Null or empty enumerable is allowed.</param>
    /// <param name="providerIdResolver">
    /// The shared provider-id resolver snapshot, or null to use <see cref="DatabaseProviderIdResolver.Empty"/>.
    /// Registration keys and lookup keys are canonicalized through it, so a bootstrap alias and the
    /// canonical id select the same registration. Resolving an alias never implies that a lock
    /// provider exists for it: an unregistered capability still fails closed with
    /// <see cref="WellKnownMigrationErrorCodes.LockNotSupported"/>.
    /// </param>
    /// <exception cref="ArgumentException">A provider is null or a provider ID is already registered.</exception>
    public DatabaseMigrationLockProviderRegistry(
        IEnumerable<IDatabaseMigrationLockProvider>? providers = null,
        DatabaseProviderIdResolver? providerIdResolver = null)
    {
        registrations = new Dictionary<string, IDatabaseMigrationLockProvider>(StringComparer.OrdinalIgnoreCase);
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

            if (!this.providerIdResolver.TryCanonicalize(provider.ProviderId, out var providerId))
            {
                throw new ArgumentException(
                    "Provider ID must be a syntactically valid database provider id.",
                    nameof(providers));
            }

            if (registrations.ContainsKey(providerId))
            {
                throw new ArgumentException(
                    $"A migration lock provider with ID '{providerId}' is already registered.",
                    nameof(providers));
            }

            registrations.Add(providerId, provider);
        }
    }

    /// <summary>
    /// Attempts to resolve a lock provider by provider ID or registered alias (case-insensitive).
    /// </summary>
    /// <param name="providerId">The database provider ID or registered alias.</param>
    /// <param name="provider">The lock provider when found.</param>
    /// <returns>true when a lock provider is registered for the ID; otherwise, false.</returns>
    public bool TryGetProvider(string? providerId, out IDatabaseMigrationLockProvider? provider)
    {
        provider = null;

        if (!providerIdResolver.TryCanonicalize(providerId, out var canonicalProviderId))
        {
            return false;
        }

        return registrations.TryGetValue(canonicalProviderId, out provider);
    }
}

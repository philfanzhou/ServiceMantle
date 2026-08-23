namespace ServiceMantle.Migration;

/// <summary>
/// Resolves and holds a read-only migration lock provider registration table.
/// Provider IDs are matched case-insensitively.
/// </summary>
public sealed class DatabaseMigrationLockProviderRegistry
{
    private readonly Dictionary<string, IDatabaseMigrationLockProvider> registrations;

    /// <summary>
    /// Initializes a lock provider registry.
    /// </summary>
    /// <param name="providers">All lock providers to register. Null or empty enumerable is allowed.</param>
    /// <exception cref="ArgumentException">A provider is null or a provider ID is already registered.</exception>
    public DatabaseMigrationLockProviderRegistry(IEnumerable<IDatabaseMigrationLockProvider>? providers = null)
    {
        registrations = new Dictionary<string, IDatabaseMigrationLockProvider>(StringComparer.OrdinalIgnoreCase);

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

            var providerId = provider.ProviderId;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException(
                    "Provider ID cannot be null or whitespace.",
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
    /// Attempts to resolve a lock provider by provider ID (case-insensitive).
    /// </summary>
    /// <param name="providerId">The database provider ID.</param>
    /// <param name="provider">The lock provider when found.</param>
    /// <returns>true when a lock provider is registered for the ID; otherwise, false.</returns>
    public bool TryGetProvider(string? providerId, out IDatabaseMigrationLockProvider? provider)
    {
        provider = null;

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        return registrations.TryGetValue(providerId, out provider);
    }
}

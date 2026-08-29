namespace ServiceMantle.Bootstrap;

/// <summary>
/// Holds an immutable snapshot that maps registered database provider ids and aliases to the
/// canonical <see cref="BootstrapDatabaseProviderDescriptor.Id"/> that declared them.
/// </summary>
/// <remarks>
/// <para>
/// Aliases are a public part of <see cref="BootstrapDatabaseProviderDescriptor"/>, so every
/// component that reads a provider id out of a bootstrap file, dispatches to a provider, or looks
/// up a keyed capability must agree on the same alias table. This type is that single shared
/// table: <see cref="BootstrapFileStore"/> canonicalizes persisted ids through it, and
/// <see cref="DatabaseTargetPreparationProviderRegistry"/> and
/// <c>DatabaseMigrationLockProviderRegistry</c> resolve their lookup keys through it instead of
/// re-implementing alias rules.
/// </para>
/// <para>
/// The snapshot is fixed at construction. Matching is <see cref="StringComparer.OrdinalIgnoreCase"/>,
/// but the returned casing is always the descriptor's own <see cref="BootstrapDatabaseProviderDescriptor.Id"/>.
/// </para>
/// <para>
/// Ids that are syntactically valid but absent from the snapshot are returned normalized (trimmed)
/// and unchanged. The resolver never guesses that an unregistered string is an alias of something
/// else, and resolving an alias never implies that any capability is registered for it.
/// </para>
/// </remarks>
public sealed class DatabaseProviderIdResolver
{
    private readonly Dictionary<string, string> canonicalIds;

    /// <summary>
    /// Initializes a resolver snapshot from provider descriptors.
    /// </summary>
    /// <param name="descriptors">The descriptors whose ids and aliases form the snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptors"/> or one of its items is null.</exception>
    /// <exception cref="ArgumentException">A canonical id or alias is declared more than once.</exception>
    public DatabaseProviderIdResolver(IEnumerable<BootstrapDatabaseProviderDescriptor> descriptors)
        : this(BuildSnapshot(descriptors, nameof(descriptors)))
    {
    }

    private DatabaseProviderIdResolver(Dictionary<string, string> canonicalIds) =>
        this.canonicalIds = canonicalIds;

    /// <summary>
    /// Gets a resolver snapshot that contains no registrations.
    /// </summary>
    /// <remarks>
    /// An empty snapshot still normalizes syntax; it simply resolves every syntactically valid id
    /// to itself, which is the correct behavior when no provider has declared aliases.
    /// </remarks>
    public static DatabaseProviderIdResolver Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the number of registered canonical ids and aliases in this snapshot.
    /// </summary>
    public int Count => canonicalIds.Count;

    /// <summary>
    /// Resolves a registered canonical id or alias to the declaring descriptor's canonical id.
    /// </summary>
    /// <param name="providerId">The provider id or alias to resolve.</param>
    /// <param name="canonicalProviderId">
    /// The declaring descriptor's canonical id when the value is registered; otherwise, null.
    /// </param>
    /// <returns>true when the value is registered in this snapshot; otherwise, false.</returns>
    public bool TryResolveRegisteredId(string? providerId, out string? canonicalProviderId)
    {
        canonicalProviderId = null;

        if (!DatabaseProviderId.TryNormalize(providerId, out var normalizedProviderId))
        {
            return false;
        }

        if (!canonicalIds.TryGetValue(normalizedProviderId, out var resolvedProviderId))
        {
            return false;
        }

        canonicalProviderId = resolvedProviderId;
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether the provider id is a registered canonical id or alias.
    /// </summary>
    /// <param name="providerId">The provider id or alias.</param>
    public bool IsRegistered(string? providerId) =>
        TryResolveRegisteredId(providerId, out _);

    /// <summary>
    /// Normalizes a provider id and resolves it when it is a registered canonical id or alias.
    /// </summary>
    /// <param name="providerId">The provider id or alias.</param>
    /// <param name="canonicalProviderId">
    /// The declaring descriptor's canonical id for a registered value, the normalized input for a
    /// syntactically valid but unregistered value, or <see cref="string.Empty"/> when the syntax is
    /// invalid.
    /// </param>
    /// <returns>true when the provider id has valid syntax; otherwise, false.</returns>
    public bool TryCanonicalize(string? providerId, out string canonicalProviderId)
    {
        if (!DatabaseProviderId.TryNormalize(providerId, out var normalizedProviderId))
        {
            canonicalProviderId = string.Empty;
            return false;
        }

        canonicalProviderId = canonicalIds.TryGetValue(normalizedProviderId, out var resolvedProviderId)
            ? resolvedProviderId
            : normalizedProviderId;

        return true;
    }

    /// <summary>
    /// Normalizes and resolves a provider id, failing when the syntax is invalid.
    /// </summary>
    /// <param name="providerId">The provider id or alias.</param>
    /// <returns>
    /// The declaring descriptor's canonical id for a registered value, or the normalized input for
    /// a syntactically valid but unregistered value.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="providerId"/> is null.</exception>
    /// <exception cref="ArgumentException">The provider id syntax is invalid.</exception>
    public string Canonicalize(string providerId)
    {
        var normalizedProviderId = DatabaseProviderId.Normalize(providerId, nameof(providerId));

        return canonicalIds.TryGetValue(normalizedProviderId, out var resolvedProviderId)
            ? resolvedProviderId
            : normalizedProviderId;
    }

    internal static DatabaseProviderIdResolver Create(
        IEnumerable<BootstrapDatabaseProviderDescriptor> descriptors,
        string parameterName) =>
        new(BuildSnapshot(descriptors, parameterName));

    private static Dictionary<string, string> BuildSnapshot(
        IEnumerable<BootstrapDatabaseProviderDescriptor> descriptors,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(descriptors, parameterName);

        // Materialize once: the snapshot must not depend on a lazy sequence being stable.
        var descriptorList = descriptors.ToList();
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Canonical ids are claimed first so that an alias colliding with a canonical id is
        // rejected regardless of the order the descriptors were supplied in.
        foreach (var descriptor in descriptorList)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(parameterName, "Provider descriptor cannot be null.");
            }

            if (snapshot.ContainsKey(descriptor.Id))
            {
                throw new ArgumentException(
                    $"The provider id '{descriptor.Id}' is already registered.",
                    parameterName);
            }

            snapshot.Add(descriptor.Id, descriptor.Id);
        }

        foreach (var descriptor in descriptorList)
        {
            foreach (var alias in descriptor.Aliases)
            {
                if (snapshot.TryGetValue(alias, out var existingCanonicalId))
                {
                    if (!string.Equals(existingCanonicalId, descriptor.Id, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"The alias '{alias}' conflicts with a registered provider id.",
                            parameterName);
                    }

                    // The descriptor repeated its own canonical id or alias; the mapping already holds.
                    continue;
                }

                snapshot.Add(alias, descriptor.Id);
            }
        }

        return snapshot;
    }
}

namespace ServiceMantle.Bootstrap;

/// <summary>Captures independent deployment capabilities using the shared provider-id resolver.</summary>
public sealed class DatabaseDeploymentCapabilityRegistry
{
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly DatabaseProviderIdResolver resolver;

    /// <summary>Creates a registry; aliases never imply an unregistered deployment capability.</summary>
    public DatabaseDeploymentCapabilityRegistry(
        IEnumerable<IDatabaseDeploymentCapabilityProvider>? providers,
        DatabaseProviderIdResolver providerIdResolver)
    {
        ArgumentNullException.ThrowIfNull(providerIdResolver);
        resolver = providerIdResolver;
        foreach (var provider in providers ?? [])
        {
            if (provider is null) throw new ArgumentException("A deployment capability provider cannot be null.", nameof(providers));
            var declaration = provider.Capability ?? throw new ArgumentException("A deployment capability declaration cannot be null.", nameof(providers));
            var canonicalId = resolver.Canonicalize(declaration.ProviderId);
            var captured = new DatabaseDeploymentCapability(canonicalId, declaration.Support);
            if (!registrations.TryAdd(canonicalId, new(captured, provider)))
                throw new ArgumentException("A deployment capability is already registered for this provider.", nameof(providers));
        }
    }

    /// <summary>Looks up a captured capability by canonical ID or registered alias.</summary>
    public bool TryGetCapability(string? providerId, out DatabaseDeploymentCapability? capability)
    {
        capability = null;
        if (!TryGetRegistration(providerId, out var registration)) return false;
        capability = registration!.Capability;
        return true;
    }

    internal bool TryGetRegistration(string? providerId, out Registration? registration)
    {
        registration = null;
        return resolver.TryCanonicalize(providerId, out var canonicalId) &&
            registrations.TryGetValue(canonicalId, out registration);
    }

    internal sealed record Registration(DatabaseDeploymentCapability Capability, IDatabaseDeploymentCapabilityProvider Provider);
}

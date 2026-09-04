namespace ServiceMantle.Bootstrap;

/// <summary>The deployment mode explicitly chosen by the consuming service.</summary>
public enum DatabaseDeploymentMode
{
    /// <summary>No deployment decision was supplied; validation fails closed.</summary>
    Unspecified,
    /// <summary>One process owns this target; no distributed migration lease is requested.</summary>
    SingleInstance,
    /// <summary>Multiple processes may use this target; a real migration lock is required.</summary>
    MultiInstance
}

/// <summary>The deployment modes explicitly supported by a provider.</summary>
public enum DatabaseDeploymentSupport
{
    /// <summary>Only an explicitly single-instance deployment is supported.</summary>
    SingleInstanceOnly,
    /// <summary>Single-instance and multi-instance deployments are supported.</summary>
    SingleAndMultiInstance
}

/// <summary>An immutable, independent provider deployment capability declaration.</summary>
public sealed class DatabaseDeploymentCapability
{
    /// <summary>Creates a declaration without implying bootstrap, preparation, or lock support.</summary>
    public DatabaseDeploymentCapability(string providerId, DatabaseDeploymentSupport support)
    {
        ProviderId = DatabaseProviderId.Normalize(providerId, nameof(providerId));
        if (!Enum.IsDefined(support)) throw new ArgumentOutOfRangeException(nameof(support));
        Support = support;
    }

    /// <summary>Gets the canonical provider identifier.</summary>
    public string ProviderId { get; }
    /// <summary>Gets the supported deployment modes.</summary>
    public DatabaseDeploymentSupport Support { get; }
}

/// <summary>Declares deployment support and supplies the provider's canonical target identity.</summary>
public interface IDatabaseDeploymentCapabilityProvider
{
    /// <summary>Gets an immutable declaration captured when the registry is constructed.</summary>
    DatabaseDeploymentCapability Capability { get; }

    /// <summary>Resolves a stable identity for process-local single-instance serialization.</summary>
    /// <remarks>
    /// Called only after deployment validation succeeds. The provider owns read-only normalization;
    /// it must not create or migrate the target. Equivalent targets must return ordinally identical
    /// values independent of credentials and connection-string spelling. Different targets must
    /// remain distinct. The value is not logged or returned in orchestration results. This does
    /// not prove filesystem or server identity against external replacement or malicious aliases.
    /// </remarks>
    ValueTask<string> GetCanonicalTargetIdentityAsync(
        BootstrapDatabaseConfiguration target, CancellationToken cancellationToken);
}

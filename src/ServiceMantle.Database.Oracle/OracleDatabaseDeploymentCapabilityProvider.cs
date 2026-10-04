using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.Oracle;

/// <summary>Declares Oracle deployment support without providing a single-instance schema identity.</summary>
/// <remarks>The built-in single-instance path deliberately returns an unsupported identity.
/// Use multi-instance with a real lock, or replace this declaration with a custom capability.</remarks>
public sealed class OracleDatabaseDeploymentCapabilityProvider : IDatabaseDeploymentCapabilityProvider
{
    /// <summary>Gets the independent deployment declaration. It does not imply single-instance identity availability.</summary>
    public DatabaseDeploymentCapability Capability { get; } =
        new(WellKnownDatabaseProviderIds.Oracle, DatabaseDeploymentSupport.SingleAndMultiInstance);

    /// <summary>Returns empty after argument and caller-cancellation checks, without parsing credentials or performing I/O.</summary>
    public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.Empty);
    }
}

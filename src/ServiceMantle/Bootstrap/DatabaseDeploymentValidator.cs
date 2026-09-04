using ServiceMantle.Migration;

namespace ServiceMantle.Bootstrap;

/// <summary>Validates deployment authorization from captured declarations without performing I/O.</summary>
public sealed class DatabaseDeploymentValidator
{
    private readonly DatabaseDeploymentCapabilityRegistry registry;

    /// <summary>Creates a validator over the immutable capability registry.</summary>
    public DatabaseDeploymentValidator(DatabaseDeploymentCapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    /// <summary>
    /// Checks explicit mode and capability only. Call before preparation, migration, Setup, or any
    /// other side effect; this method never resolves a target or calls a provider.
    /// </summary>
    public DatabaseDeploymentValidationResult Validate(string? providerId, DatabaseDeploymentMode mode)
    {
        var allowed = mode is DatabaseDeploymentMode.SingleInstance or DatabaseDeploymentMode.MultiInstance &&
            registry.TryGetCapability(providerId, out var capability) &&
            (mode == DatabaseDeploymentMode.SingleInstance ||
                capability!.Support == DatabaseDeploymentSupport.SingleAndMultiInstance);
        return new(allowed);
    }
}

/// <summary>A value-free result with the existing preparation and migration failure mappings.</summary>
public sealed class DatabaseDeploymentValidationResult
{
    internal DatabaseDeploymentValidationResult(bool supported) => IsSupported = supported;
    /// <summary>Gets whether this explicit deployment mode is supported.</summary>
    public bool IsSupported { get; }
    /// <summary>Gets the preparation failure mapping, or null when supported.</summary>
    public string? PreparationErrorCode => IsSupported ? null : WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported;
    /// <summary>Gets the migration failure mapping, or null when supported.</summary>
    public string? MigrationErrorCode => IsSupported ? null : WellKnownMigrationErrorCodes.LockNotSupported;
    /// <summary>Returns only the validation outcome, with no target or connection information.</summary>
    public override string ToString() => $"DatabaseDeploymentValidationResult(IsSupported={IsSupported})";
}

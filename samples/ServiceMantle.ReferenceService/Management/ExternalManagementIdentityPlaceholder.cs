using ServiceMantle.Management;

namespace ServiceMantle.ReferenceService.Management;

/// <summary>An unconfigured external provider, never a source of local administrators.</summary>
public sealed class ExternalManagementIdentityPlaceholder : IManagementIdentityProvider
{
    public ValueTask<ManagementIdentityResult> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ManagementIdentityResult.Failed("reference.external_identity_not_configured"));
    }
}

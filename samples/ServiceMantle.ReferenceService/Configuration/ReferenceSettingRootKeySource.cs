using ServiceMantle.Configuration;
using ServiceMantle.ReferenceService.Management;

namespace ServiceMantle.ReferenceService.Configuration;

/// <summary>
/// Supplies the deployment's single external root key for the setting snapshot stack, reusing the
/// management root key rather than introducing a second deployment secret.
/// </summary>
/// <remarks>
/// The library isolates uses by derivation context - the setting values' purpose is their
/// definition key, while the key ring's purpose is its repository element id - so a second key
/// would add a deployment secret without adding isolation. The key's presence and length are
/// validated by <see cref="ReferenceManagementOptions.Read"/> before any database or network side
/// effect.
/// </remarks>
internal sealed class ReferenceSettingRootKeySource(ReferenceManagementOptions management)
    : IServiceSettingRootKeySource
{
    public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(management.RootKey);
    }
}

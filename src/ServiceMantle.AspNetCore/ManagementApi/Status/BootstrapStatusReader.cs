using ServiceMantle.Bootstrap;

namespace ServiceMantle.AspNetCore.ManagementApi.Status;

/// <summary>
/// The single boundary through which the anonymous installation status entry may observe the local
/// Bootstrap file.
/// </summary>
/// <remarks>
/// <c>BootstrapManagementStatus</c> also carries the ServiceId, the InstanceId, the configured
/// provider, the server version, and two configured-secret flags. None of those may be revealed to
/// an anonymous caller, so exactly one bit crosses this boundary and the projection object itself
/// never leaves it.
/// </remarks>
internal interface IBootstrapStatusReader
{
    /// <summary>Reports whether a valid local Bootstrap file currently exists.</summary>
    /// <exception cref="BootstrapException">The existing file is damaged, mismatched, or unreadable.</exception>
    bool IsBootstrapConfigured();
}

/// <summary>Reads the local Bootstrap presence through the existing management projection.</summary>
internal sealed class BootstrapStatusReader(BootstrapConfigurationManager manager)
    : IBootstrapStatusReader
{
    public bool IsBootstrapConfigured() => manager.GetStatus().IsConfigured;
}

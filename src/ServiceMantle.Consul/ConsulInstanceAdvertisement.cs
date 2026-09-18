using ServiceMantle.Discovery;

namespace ServiceMantle.Consul;

/// <summary>
/// The validated immutable per-instance advertisement copy the client provider captures into
/// every session, shaped like <see cref="ConsulLifecycleSettings"/>.
/// </summary>
/// <remarks>
/// An unconfigured copy (<see cref="IsConfigured"/> false) means the service-level values keep
/// their exact existing meaning. Two copies are equal exactly when they agree on being configured
/// and on both values, which is what repeated registrations must do to stay idempotent.
/// </remarks>
internal sealed record ConsulInstanceAdvertisement(string? Address, int? Port)
{
    internal static readonly ConsulInstanceAdvertisement Unconfigured = new(null, null);

    internal bool IsConfigured => Address is not null;

    /// <summary>
    /// Validates and copies the caller's explicit statement. Called by the Consul registration
    /// entry before any descriptor that could reach a registration is written, with exactly the
    /// rules the service-level values obey - the same validators, not a second rule set.
    /// </summary>
    /// <exception cref="ConsulConfigurationException">
    /// Only one of the address and the port was supplied, or a value is out of range.</exception>
    internal static ConsulInstanceAdvertisement FromOptions(ServiceInstanceAdvertisementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var address = options.Address;
        var port = options.Port;
        if (address is null && port is null)
        {
            return Unconfigured;
        }

        // One without the other is not half an advertisement: it is a conflict.
        if (address is null || port is null ||
            !ConsulSnapshotBinding.IsValidAdvertisedAddress(address) ||
            !ConsulSnapshotBinding.IsValidAdvertisedPort(port.Value))
        {
            throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
        }

        return new ConsulInstanceAdvertisement(address, port);
    }
}

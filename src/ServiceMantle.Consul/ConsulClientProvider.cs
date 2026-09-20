using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

/// <summary>Creates explicit owned sessions from a single atomically captured active snapshot.</summary>
public sealed class ConsulClientProvider
{
    private readonly Func<IServiceSettingCurrentSnapshotAccessor> accessor;
    private readonly ServiceId serviceId;
    private readonly InstanceId instanceId;
    private readonly Func<IConsulClientFactory> factory;
    private readonly ConsulInstanceAdvertisement advertisement;

    internal ConsulClientProvider(Func<IServiceSettingCurrentSnapshotAccessor> accessor, ServiceId serviceId,
        InstanceId instanceId, Func<IConsulClientFactory> factory, ConsulInstanceAdvertisement? advertisement)
    {
        this.accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        this.serviceId = serviceId;
        this.instanceId = instanceId;
        this.factory = factory;
        this.advertisement = advertisement ?? ConsulInstanceAdvertisement.Unconfigured;
    }

    /// <summary>
    /// Captures one active snapshot. Returns null when disabled, without resolving the factory.
    /// The caller owns the returned session and must dispose it. This method performs no network I/O.
    /// </summary>
    /// <exception cref="ConsulConfigurationException">The snapshot or client factory failed.</exception>
    public ConsulClientSession? CreateClient()
    {
        ServiceSettingSnapshot snapshot;
        try
        {
            // The accessor getter is part of the same safe boundary: a typed registration that is
            // missing, fails to resolve, or returns an unusable accessor is the closed snapshot-
            // unavailable category, never a raw dependency exception.
            if (!accessor().TryGetCurrent(out var current) || current is null)
            {
                throw new ConsulConfigurationException(ConsulConfigurationError.SnapshotUnavailable);
            }
            snapshot = current;
        }
        catch (ConsulConfigurationException) { throw; }
        catch { throw new ConsulConfigurationException(ConsulConfigurationError.SnapshotUnavailable); }

        ConsulSnapshotBinding? binding;
        try
        {
            if (snapshot.ServiceId != serviceId) { throw ConsulSnapshotBinding.Invalid(); }
            binding = ConsulSnapshotBinding.Read(
                snapshot.Values,
                advertisement.IsConfigured ? advertisement.Address : null,
                advertisement.IsConfigured ? advertisement.Port : null);
        }
        catch { throw ConsulSnapshotBinding.Invalid(); }
        if (binding is null) { return null; }

        var registration = new ConsulServiceRegistration(serviceId, instanceId, binding);
        var configuration = new ConsulClientConfiguration(snapshot.Version, binding);
        try
        {
            var client = factory().Create(configuration);
            if (client is null) { throw new InvalidOperationException(); }
            return new ConsulClientSession(client, registration, snapshot.Version);
        }
        catch { throw new ConsulConfigurationException(ConsulConfigurationError.ClientCreationFailed); }
    }

    /// <summary>
    /// Accepts repeated advertisement registrations only while they agree, exactly like the
    /// timing copies; an unconfigured advertisement alone stays valid.
    /// </summary>
    internal static ConsulInstanceAdvertisement SingleAdvertisement(
        IEnumerable<ConsulInstanceAdvertisement> registered)
    {
        ConsulInstanceAdvertisement? baseline = null;
        foreach (var current in registered)
        {
            if (baseline is not null && baseline != current)
            {
                throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
            }

            baseline = current;
        }

        return baseline ?? ConsulInstanceAdvertisement.Unconfigured;
    }
}

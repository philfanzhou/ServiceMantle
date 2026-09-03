using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

/// <summary>Creates explicit owned sessions from a single atomically captured active snapshot.</summary>
public sealed class ConsulClientProvider
{
    private readonly IServiceSettingCurrentSnapshotAccessor accessor;
    private readonly ServiceId serviceId;
    private readonly InstanceId instanceId;
    private readonly Func<IConsulClientFactory> factory;

    internal ConsulClientProvider(IServiceSettingCurrentSnapshotAccessor accessor, ServiceId serviceId,
        InstanceId instanceId, Func<IConsulClientFactory> factory)
    {
        this.accessor = accessor;
        this.serviceId = serviceId;
        this.instanceId = instanceId;
        this.factory = factory;
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
            if (!accessor.TryGetCurrent(out var current) || current is null)
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
            binding = ConsulSnapshotBinding.Read(snapshot.Values);
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
}

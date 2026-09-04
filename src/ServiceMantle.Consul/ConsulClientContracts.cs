using System.Text;

namespace ServiceMantle.Consul;

/// <summary>Contains one immutable, credential-free agent service registration.</summary>
public sealed class ConsulServiceRegistration
{
    internal ConsulServiceRegistration(ServiceId serviceId, InstanceId instanceId, ConsulSnapshotBinding binding)
    {
        // JSON and URI encoders replace malformed UTF-16. Reject it before transport so two
        // different instance identifiers cannot collapse to the same replacement-character ID.
        try { _ = new UTF8Encoding(false, true).GetByteCount(instanceId.Value); }
        catch (EncoderFallbackException) { throw ConsulSnapshotBinding.Invalid(); }
        Id = serviceId.Value + ":" + instanceId.Value;
        Name = binding.Name;
        Address = binding.Address;
        Port = binding.Port;
        HealthUri = binding.HealthUri;
    }

    /// <summary>Gets the service and instance identity combined without losing either component.</summary>
    public string Id { get; }
    /// <summary>Gets the configured Consul service name.</summary>
    public string Name { get; }
    /// <summary>Gets the advertised address.</summary>
    public string Address { get; }
    /// <summary>Gets the advertised port.</summary>
    public int Port { get; }
    /// <summary>Gets the same-origin HTTP(S) health URL.</summary>
    public Uri HealthUri { get; }
    /// <summary>Returns metadata only.</summary>
    public override string ToString() => "ConsulServiceRegistration";
}

/// <summary>Supplies one validated agent configuration to a trusted client factory.</summary>
public sealed class ConsulClientConfiguration
{
    private readonly string? token;
    internal ConsulClientConfiguration(long version, ConsulSnapshotBinding binding)
    {
        SnapshotVersion = version;
        Endpoint = binding.Endpoint;
        token = binding.Token;
    }
    /// <summary>Gets the captured setting version.</summary>
    public long SnapshotVersion { get; }
    /// <summary>Gets the agent root URI, without user-info, query or fragment.</summary>
    public Uri Endpoint { get; }
    /// <summary>Gets whether the captured snapshot contains an ACL token.</summary>
    public bool HasToken => token is not null;
    /// <summary>Explicitly accesses the secret for transport only. Never log or project this return value.</summary>
    public string? GetToken() => token;
    /// <summary>Returns only version and credential-presence metadata.</summary>
    public override string ToString() => $"ConsulClientConfiguration(Version={SnapshotVersion}, HasToken={HasToken})";
}

/// <summary>Classifies one explicit client operation without remote response or exception text.</summary>
public enum ConsulClientResult
{
    /// <summary>The agent accepted the operation.</summary>
    Success,
    /// <summary>The agent returned a non-success HTTP status.</summary>
    Rejected,
    /// <summary>The operation failed, timed out, or was internally cancelled.</summary>
    Unavailable
}

/// <summary>Replaces the single-call Consul transport; it must not start lifecycle or retry work.</summary>
public interface IConsulClient : IDisposable
{
    /// <summary>Registers the supplied service once. The caller owns readiness gating.</summary>
    ValueTask<ConsulClientResult> RegisterAsync(ConsulServiceRegistration registration, CancellationToken cancellationToken = default);
    /// <summary>Deregisters the supplied service ID once.</summary>
    ValueTask<ConsulClientResult> DeregisterAsync(string registrationId, CancellationToken cancellationToken = default);
}

/// <summary>Creates an owned client without issuing network requests or starting background work.</summary>
public interface IConsulClientFactory
{
    /// <summary>Creates a client for the captured validated configuration.</summary>
    IConsulClient Create(ConsulClientConfiguration configuration);
}

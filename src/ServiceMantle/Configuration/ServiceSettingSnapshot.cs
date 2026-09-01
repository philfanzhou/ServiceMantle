using System.Collections.ObjectModel;

namespace ServiceMantle.Configuration;

/// <summary>
/// Contains one complete immutable set of materialized service settings.
/// </summary>
public sealed class ServiceSettingSnapshot
{
    private readonly byte[] normalizedFingerprint;

    internal ServiceSettingSnapshot(
        ServiceId serviceId,
        long version,
        IReadOnlyDictionary<string, ServiceSettingValue> values,
        byte[] normalizedFingerprint)
    {
        ServiceId = serviceId;
        Version = version;
        Values = new ReadOnlyDictionary<string, ServiceSettingValue>(
            new Dictionary<string, ServiceSettingValue>(values, StringComparer.OrdinalIgnoreCase));
        this.normalizedFingerprint = normalizedFingerprint;
    }

    /// <summary>Gets the service represented by this snapshot.</summary>
    public ServiceId ServiceId { get; }

    /// <summary>Gets the persisted service-level version.</summary>
    public long Version { get; }

    /// <summary>Gets the complete immutable values indexed by normalized stable key.</summary>
    public IReadOnlyDictionary<string, ServiceSettingValue> Values { get; }

    internal ReadOnlySpan<byte> NormalizedFingerprint => normalizedFingerprint;

    /// <summary>Returns metadata only and never includes materialized values.</summary>
    public override string ToString() =>
        $"ServiceSettingSnapshot(ServiceId={ServiceId.Value}, Version={Version}, ValueCount={Values.Count})";
}

/// <summary>Exposes the atomically activated setting snapshot.</summary>
public interface IServiceSettingCurrentSnapshotAccessor
{
    /// <summary>
    /// Gets the current snapshot when one has been activated; otherwise returns false.
    /// </summary>
    bool TryGetCurrent(out ServiceSettingSnapshot? snapshot);
}

/// <summary>Stores one process-local setting snapshot by atomic reference replacement.</summary>
public sealed class ServiceSettingCurrentSnapshotAccessor : IServiceSettingCurrentSnapshotAccessor
{
    private ServiceSettingSnapshot? current;

    /// <inheritdoc />
    public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot)
    {
        snapshot = Volatile.Read(ref current);
        return snapshot is not null;
    }

    internal void Publish(ServiceSettingSnapshot snapshot) => Volatile.Write(ref current, snapshot);
}

namespace ServiceMantle.Configuration;

/// <summary>
/// Describes one persisted setting value before decryption and materialization.
/// </summary>
public sealed class PersistedServiceSettingValue
{
    /// <summary>Initializes one untrusted persisted value.</summary>
    public PersistedServiceSettingValue(
        string key,
        long version,
        ServiceSettingValueType valueType,
        string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Key = key;
        Version = version;
        ValueType = valueType;
        Value = value;
    }

    /// <summary>Gets the persisted key. The loader validates and normalizes it.</summary>
    public string Key { get; }

    /// <summary>Gets the service-level version attached to this value.</summary>
    public long Version { get; }

    /// <summary>Gets the persisted value type.</summary>
    public ServiceSettingValueType ValueType { get; }

    /// <summary>Gets the raw value or protected envelope.</summary>
    public string Value { get; }

    /// <summary>Returns metadata only and never includes the persisted value.</summary>
    public override string ToString() =>
        $"PersistedServiceSettingValue(Version={Version}, ValueType={ValueType})";
}

/// <summary>
/// Contains one complete, untrusted persisted service-setting read.
/// </summary>
public sealed class ServiceSettingSnapshotRead
{
    /// <summary>Initializes a persisted snapshot read.</summary>
    public ServiceSettingSnapshotRead(
        ServiceId serviceId,
        long version,
        IEnumerable<PersistedServiceSettingValue> values)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(values);
        ServiceId = serviceId;
        Version = version;
        Values = values.ToList().AsReadOnly();
    }

    /// <summary>Gets the service identity returned by the source.</summary>
    public ServiceId ServiceId { get; }

    /// <summary>Gets the declared service-level version.</summary>
    public long Version { get; }

    /// <summary>Gets an immutable copy of the persisted values.</summary>
    public IReadOnlyList<PersistedServiceSettingValue> Values { get; }

    /// <summary>Returns metadata only and never includes persisted values.</summary>
    public override string ToString() =>
        $"ServiceSettingSnapshotRead(ServiceId={ServiceId.Value}, Version={Version}, ValueCount={Values.Count})";
}

/// <summary>
/// Reads one complete persisted setting version without assuming a database provider.
/// </summary>
public interface IServiceSettingSnapshotSource
{
    /// <summary>Reads the complete persisted setting version for one service.</summary>
    ValueTask<ServiceSettingSnapshotRead> LoadAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies the external root key used only while sensitive settings are materialized.
/// </summary>
public interface IServiceSettingRootKeySource
{
    /// <summary>Gets the current external root key.</summary>
    ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the shared raw setting store to the typed snapshot-source contract.
/// </summary>
public sealed class ServiceSettingStoreSnapshotSource : IServiceSettingSnapshotSource
{
    private readonly IServiceSettingStore store;
    private readonly ServiceSettingDefinitionRegistry registry;

    /// <summary>Initializes the adapter.</summary>
    public ServiceSettingStoreSnapshotSource(
        IServiceSettingStore store,
        ServiceSettingDefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        this.store = store;
        this.registry = registry;
    }

    /// <inheritdoc />
    public async ValueTask<ServiceSettingSnapshotRead> LoadAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var values = snapshot.Values.Select(pair => new PersistedServiceSettingValue(
            pair.Key,
            snapshot.Version,
            registry.TryGetDefinition(pair.Key, out var definition)
                ? definition!.ValueType
                : ServiceSettingValueType.String,
            pair.Value));
        return new ServiceSettingSnapshotRead(snapshot.ServiceId, snapshot.Version, values);
    }
}

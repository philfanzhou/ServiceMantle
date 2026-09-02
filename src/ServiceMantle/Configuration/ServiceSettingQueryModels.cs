namespace ServiceMantle.Configuration;

/// <summary>Identifies where one materialized setting value came from.</summary>
public enum ServiceSettingValueSource
{
    /// <summary>No persisted value or definition default is present.</summary>
    Missing,

    /// <summary>The value came from the registered definition default.</summary>
    Default,

    /// <summary>The value came from the persisted setting snapshot.</summary>
    Persisted
}

/// <summary>Contains safe immutable metadata for one registered setting definition.</summary>
public sealed class ServiceSettingDefinitionProjection
{
    internal ServiceSettingDefinitionProjection(ServiceSettingDefinition definition)
    {
        Key = definition.Key;
        ValueType = definition.ValueType;
        IsRequired = definition.IsRequired;
        IsSensitive = definition.IsSensitive;
        HasDefault = definition.DefaultValue is not null;
        RequiresRestart = definition.RequiresRestart;
    }

    /// <summary>Gets the normalized stable key.</summary>
    public string Key { get; }

    /// <summary>Gets the value representation.</summary>
    public ServiceSettingValueType ValueType { get; }

    /// <summary>Gets a value indicating whether a value is required.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets a value indicating whether the materialized value is sensitive.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets a value indicating whether the definition has a default.</summary>
    public bool HasDefault { get; }

    /// <summary>Gets a value indicating whether changing the setting requires a restart.</summary>
    public bool RequiresRestart { get; }

    /// <summary>Returns safe definition metadata without defaults or constraints.</summary>
    public override string ToString() =>
        $"ServiceSettingDefinitionProjection(Key={Key}, ValueType={ValueType}, " +
        $"IsRequired={IsRequired}, IsSensitive={IsSensitive}, HasDefault={HasDefault}, " +
        $"RequiresRestart={RequiresRestart})";
}

/// <summary>Contains the safe immutable current-value projection for one setting.</summary>
public sealed class ServiceSettingCurrentValueProjection
{
    internal ServiceSettingCurrentValueProjection(
        ServiceSettingDefinitionProjection definition,
        bool hasValue,
        ServiceSettingValueSource source,
        string? value)
    {
        Key = definition.Key;
        ValueType = definition.ValueType;
        IsRequired = definition.IsRequired;
        IsSensitive = definition.IsSensitive;
        HasDefault = definition.HasDefault;
        RequiresRestart = definition.RequiresRestart;
        HasValue = hasValue;
        Source = source;
        Value = value;
    }

    /// <summary>Gets the normalized stable key.</summary>
    public string Key { get; }

    /// <summary>Gets the value representation.</summary>
    public ServiceSettingValueType ValueType { get; }

    /// <summary>Gets a value indicating whether a value is required.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets a value indicating whether the materialized value is sensitive.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets a value indicating whether the definition has a default.</summary>
    public bool HasDefault { get; }

    /// <summary>Gets a value indicating whether changing the setting requires a restart.</summary>
    public bool RequiresRestart { get; }

    /// <summary>Gets a value indicating whether a materialized value is present.</summary>
    public bool HasValue { get; }

    /// <summary>Gets the source of the materialized value.</summary>
    public ServiceSettingValueSource Source { get; }

    /// <summary>
    /// Gets the normalized non-sensitive value, or null when the value is missing or sensitive.
    /// </summary>
    public string? Value { get; }

    /// <summary>Returns safe metadata without the projected value.</summary>
    public override string ToString() =>
        $"ServiceSettingCurrentValueProjection(Key={Key}, ValueType={ValueType}, " +
        $"IsRequired={IsRequired}, IsSensitive={IsSensitive}, HasDefault={HasDefault}, " +
        $"RequiresRestart={RequiresRestart}, HasValue={HasValue}, Source={Source})";
}

/// <summary>Contains the closed result of one current-setting query.</summary>
public sealed class ServiceSettingCurrentQueryResult
{
    private ServiceSettingCurrentQueryResult(
        long? version,
        IReadOnlyList<ServiceSettingCurrentValueProjection> values,
        IReadOnlyList<ServiceSettingSnapshotError> errors)
    {
        Version = version;
        Values = values;
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether one complete snapshot was projected.</summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>Gets the successful snapshot version, or null on failure.</summary>
    public long? Version { get; }

    /// <summary>Gets all current values in stable normalized-key order, or an empty list on failure.</summary>
    public IReadOnlyList<ServiceSettingCurrentValueProjection> Values { get; }

    /// <summary>Gets safe snapshot failures; the collection is empty on success.</summary>
    public IReadOnlyList<ServiceSettingSnapshotError> Errors { get; }

    internal static ServiceSettingCurrentQueryResult Success(
        long version,
        IReadOnlyList<ServiceSettingCurrentValueProjection> values) =>
        new(version, values, []);

    internal static ServiceSettingCurrentQueryResult Failure(
        IReadOnlyList<ServiceSettingSnapshotError> errors) =>
        new(null, [], errors.ToList().AsReadOnly());

    /// <summary>Returns safe result metadata without setting values.</summary>
    public override string ToString() =>
        $"ServiceSettingCurrentQueryResult(Succeeded={Succeeded}, Version={Version?.ToString() ?? "<none>"}, " +
        $"ValueCount={Values.Count}, ErrorCount={Errors.Count})";
}

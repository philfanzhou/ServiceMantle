using System.Collections.ObjectModel;

namespace ServiceMantle.Configuration;

/// <summary>
/// Stable, non-sensitive error codes emitted by shared setting persistence.
/// </summary>
public static class WellKnownServiceSettingStoreErrorCodes
{
    /// <summary>The expected service-level version did not match.</summary>
    public const string VersionConflict = "service_settings.version_conflict";

    /// <summary>A database constraint rejected the update.</summary>
    public const string ConstraintViolation = "service_settings.constraint_violation";

    /// <summary>The service-level version cannot be incremented.</summary>
    public const string VersionExhausted = "service_settings.version_exhausted";

    /// <summary>The stored aggregate is malformed or violates persistence invariants.</summary>
    public const string StorageCorrupt = "service_settings.storage_corrupt";

    /// <summary>The persistence operation failed for an unclassified storage reason.</summary>
    public const string StorageError = "service_settings.storage_error";
}

/// <summary>
/// Describes one immutable version of the persisted raw setting values for a service.
/// </summary>
public sealed class ServiceSettingStoreSnapshot
{
    /// <summary>Initializes an immutable persisted setting snapshot.</summary>
    public ServiceSettingStoreSnapshot(
        ServiceId serviceId,
        long version,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset? updatedAtUtc,
        string? updatedBy,
        bool restartRequired)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(values);
        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (version == 0 &&
            (updatedAtUtc is not null || updatedBy is not null || values.Count != 0 || restartRequired))
        {
            throw new ArgumentException("An empty setting snapshot cannot contain persisted state.");
        }

        if (version > 0 &&
            (updatedAtUtc is null ||
             updatedAtUtc.Value.Offset != TimeSpan.Zero ||
             string.IsNullOrWhiteSpace(updatedBy)))
        {
            throw new ArgumentException("A persisted setting snapshot requires UTC update metadata.");
        }

        ServiceId = serviceId;
        Version = version;
        Values = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
        UpdatedAtUtc = updatedAtUtc;
        UpdatedBy = updatedBy;
        RestartRequired = restartRequired;
    }

    /// <summary>Gets the service whose shared settings were read.</summary>
    public ServiceId ServiceId { get; }

    /// <summary>Gets the monotonic service-level version, or zero when no row exists.</summary>
    public long Version { get; }

    /// <summary>Gets an immutable copy of the persisted raw values.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Gets the UTC time of the last update, or null for an empty store.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; }

    /// <summary>Gets the caller-supplied operator identifier for the last update.</summary>
    public string? UpdatedBy { get; }

    /// <summary>Gets the restart marker recorded by the last update.</summary>
    public bool RestartRequired { get; }

    /// <summary>Returns metadata only and never includes setting values or the operator identifier.</summary>
    public override string ToString() =>
        $"ServiceSettingStoreSnapshot(ServiceId={ServiceId.Value}, Version={Version}, " +
        $"ValueCount={Values.Count}, RestartRequired={RestartRequired})";
}

/// <summary>
/// Describes one validated batch of raw setting changes and its optimistic concurrency baseline.
/// </summary>
public sealed class ServiceSettingStoreUpdate
{
    private const int MaximumOperatorIdLength = 256;

    /// <summary>Initializes a shared setting update.</summary>
    /// <param name="expectedVersion">The service-level version the caller read.</param>
    /// <param name="changes">Normalized setting keys mapped to values, or null to remove a value.</param>
    /// <param name="updatedBy">The non-secret operator identifier attributed to the update.</param>
    /// <param name="restartRequired">Whether this update requires a service restart.</param>
    public ServiceSettingStoreUpdate(
        long expectedVersion,
        IReadOnlyDictionary<string, string?> changes,
        string updatedBy,
        bool restartRequired)
    {
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(updatedBy);
        if (changes.Count == 0)
        {
            throw new ArgumentException("At least one setting change is required.", nameof(changes));
        }

        if (string.IsNullOrWhiteSpace(updatedBy) ||
            updatedBy.Length > MaximumOperatorIdLength ||
            updatedBy.Any(char.IsControl))
        {
            throw new ArgumentException("The operator identifier is invalid.", nameof(updatedBy));
        }

        var materialized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changes)
        {
            var normalizedKey = ServiceSettingValidationPrimitives.NormalizeKey(key);
            if (!materialized.TryAdd(normalizedKey, value))
            {
                throw new ArgumentException(
                    "The setting update contains duplicate normalized keys.",
                    nameof(changes));
            }
        }

        ExpectedVersion = expectedVersion;
        Changes = new ReadOnlyDictionary<string, string?>(materialized);
        UpdatedBy = updatedBy;
        RestartRequired = restartRequired;
    }

    /// <summary>Gets the expected service-level version.</summary>
    public long ExpectedVersion { get; }

    /// <summary>Gets an immutable copy of the raw value changes.</summary>
    public IReadOnlyDictionary<string, string?> Changes { get; }

    /// <summary>Gets the operator identifier attributed to the update.</summary>
    public string UpdatedBy { get; }

    /// <summary>Gets the restart marker to persist.</summary>
    public bool RestartRequired { get; }

    /// <summary>Returns metadata only and never includes values or the operator identifier.</summary>
    public override string ToString() =>
        $"ServiceSettingStoreUpdate(ExpectedVersion={ExpectedVersion}, " +
        $"ChangeCount={Changes.Count}, RestartRequired={RestartRequired})";
}

/// <summary>Represents the safe result of one shared setting update.</summary>
public sealed class ServiceSettingStoreUpdateResult
{
    private ServiceSettingStoreUpdateResult(bool succeeded, long? version, string? errorCode)
    {
        Succeeded = succeeded;
        Version = version;
        ErrorCode = errorCode;
    }

    /// <summary>Gets a value indicating whether the entire batch committed.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the committed version on success, or null on failure.</summary>
    public long? Version { get; }

    /// <summary>Gets the stable failure classification, or null on success.</summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a successful result.</summary>
    public static ServiceSettingStoreUpdateResult Success(long version)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        return new(true, version, null);
    }

    /// <summary>Creates a failed result from a well-known shared setting store code.</summary>
    public static ServiceSettingStoreUpdateResult Failure(string errorCode)
    {
        if (errorCode is not (
            WellKnownServiceSettingStoreErrorCodes.VersionConflict or
            WellKnownServiceSettingStoreErrorCodes.ConstraintViolation or
            WellKnownServiceSettingStoreErrorCodes.VersionExhausted or
            WellKnownServiceSettingStoreErrorCodes.StorageCorrupt or
            WellKnownServiceSettingStoreErrorCodes.StorageError))
        {
            throw new ArgumentException("The setting store error code is invalid.", nameof(errorCode));
        }

        return new(false, null, errorCode);
    }

    /// <summary>Returns only the version or stable error code.</summary>
    public override string ToString() =>
        Succeeded
            ? $"ServiceSettingStoreUpdateResult(Succeeded=True, Version={Version})"
            : $"ServiceSettingStoreUpdateResult(Succeeded=False, ErrorCode={ErrorCode})";
}

/// <summary>Indicates a safe shared setting read failure.</summary>
public sealed class ServiceSettingStoreException : Exception
{
    private ServiceSettingStoreException(
        string errorCode,
        string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public string ErrorCode { get; }

    /// <summary>Creates a safe read failure without exposing provider diagnostics.</summary>
    public static ServiceSettingStoreException Failure(string errorCode)
    {
        var message = errorCode switch
        {
            WellKnownServiceSettingStoreErrorCodes.StorageCorrupt =>
                "The shared setting aggregate is invalid.",
            WellKnownServiceSettingStoreErrorCodes.StorageError =>
                "The shared setting state could not be read.",
            _ => throw new ArgumentException(
                "The setting store exception code is invalid.",
                nameof(errorCode)),
        };

        return new ServiceSettingStoreException(errorCode, message);
    }

    /// <summary>Returns safe information without provider diagnostics or setting values.</summary>
    public override string ToString() =>
        $"ServiceSettingStoreException(ErrorCode={ErrorCode}, Message={Message})";
}

/// <summary>Provides shared raw setting persistence for one service at a time.</summary>
/// <remarks>
/// Implementations validate only persistence structure. Product types, required values, defaults,
/// and constraints remain the responsibility of <see cref="ServiceSettingDefinitionRegistry"/>.
/// </remarks>
public interface IServiceSettingStore
{
    /// <summary>Loads the complete persisted raw setting snapshot.</summary>
    ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically applies every change and increments the service-level version exactly once.
    /// </summary>
    ValueTask<ServiceSettingStoreUpdateResult> UpdateAsync(
        ServiceId serviceId,
        ServiceSettingStoreUpdate update,
        CancellationToken cancellationToken = default);
}

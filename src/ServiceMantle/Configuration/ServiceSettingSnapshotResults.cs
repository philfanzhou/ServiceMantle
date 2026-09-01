namespace ServiceMantle.Configuration;

/// <summary>Stable, non-sensitive failure codes emitted while loading a setting snapshot.</summary>
public static class WellKnownServiceSettingSnapshotErrorCodes
{
    /// <summary>The source returned a different service identity.</summary>
    public const string ServiceMismatch = "configuration.snapshot_service_mismatch";
    /// <summary>The source returned an invalid or mixed service-level version.</summary>
    public const string MixedVersion = "configuration.snapshot_mixed_version";
    /// <summary>More than one persisted value normalized to the same key.</summary>
    public const string DuplicateKey = "configuration.snapshot_duplicate_key";
    /// <summary>A persisted key is not registered.</summary>
    public const string UnknownKey = "configuration.snapshot_unknown_key";
    /// <summary>A required registered value is missing.</summary>
    public const string MissingRequired = "configuration.snapshot_missing_required";
    /// <summary>A persisted type or representation does not match its definition.</summary>
    public const string ValueTypeMismatch = "configuration.snapshot_type_mismatch";
    /// <summary>A sensitive value was not stored in a supported protected envelope.</summary>
    public const string SensitiveEnvelopeRequired = "configuration.snapshot_sensitive_envelope_required";
    /// <summary>The sensitive envelope version is not supported.</summary>
    public const string SensitiveVersionUnsupported = "configuration.snapshot_sensitive_version_unsupported";
    /// <summary>The sensitive envelope payload is malformed.</summary>
    public const string SensitiveCiphertextInvalid = "configuration.snapshot_sensitive_ciphertext_invalid";
    /// <summary>The sensitive value could not be authenticated for its service and key.</summary>
    public const string SensitiveAuthenticationFailed = "configuration.snapshot_sensitive_authentication_failed";
    /// <summary>External root-key material was unavailable.</summary>
    public const string SensitiveKeyUnavailable = "configuration.snapshot_sensitive_key_unavailable";
    /// <summary>A registered value or composite constraint rejected the complete candidate.</summary>
    public const string ValidationFailed = "configuration.snapshot_validation_failed";
    /// <summary>The persisted snapshot could not be read safely.</summary>
    public const string LoadFailed = "configuration.snapshot_load_failed";
    /// <summary>The loaded version is older than the active version.</summary>
    public const string Stale = "configuration.snapshot_stale";
    /// <summary>The loaded version matches but its normalized content differs.</summary>
    public const string Conflict = "configuration.snapshot_conflict";
}

/// <summary>Identifies one safe snapshot-loading failure.</summary>
public sealed record ServiceSettingSnapshotError
{
    /// <summary>Initializes a safe snapshot-loading failure.</summary>
    public ServiceSettingSnapshotError(string? key, string errorCode)
    {
        if (!IsWellKnown(errorCode))
        {
            throw new ArgumentException("The snapshot error code is invalid.", nameof(errorCode));
        }

        Key = key is null ? null : ServiceSettingValidationPrimitives.NormalizeKey(key);
        ErrorCode = errorCode;
    }

    /// <summary>Gets the normalized affected key, or null for a snapshot-wide failure.</summary>
    public string? Key { get; }

    /// <summary>Gets the stable non-sensitive error code.</summary>
    public string ErrorCode { get; }

    /// <summary>Returns only safe classification metadata.</summary>
    public override string ToString() =>
        $"ServiceSettingSnapshotError(Key={Key ?? "<snapshot>"}, ErrorCode={ErrorCode})";

    private static bool IsWellKnown(string errorCode) => errorCode is
        WellKnownServiceSettingSnapshotErrorCodes.ServiceMismatch or
        WellKnownServiceSettingSnapshotErrorCodes.MixedVersion or
        WellKnownServiceSettingSnapshotErrorCodes.DuplicateKey or
        WellKnownServiceSettingSnapshotErrorCodes.UnknownKey or
        WellKnownServiceSettingSnapshotErrorCodes.MissingRequired or
        WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch or
        WellKnownServiceSettingSnapshotErrorCodes.SensitiveEnvelopeRequired or
        WellKnownServiceSettingSnapshotErrorCodes.SensitiveVersionUnsupported or
        WellKnownServiceSettingSnapshotErrorCodes.SensitiveCiphertextInvalid or
        WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed or
        WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable or
        WellKnownServiceSettingSnapshotErrorCodes.ValidationFailed or
        WellKnownServiceSettingSnapshotErrorCodes.LoadFailed or
        WellKnownServiceSettingSnapshotErrorCodes.Stale or
        WellKnownServiceSettingSnapshotErrorCodes.Conflict;
}

/// <summary>Contains the closed result of one snapshot refresh.</summary>
public sealed class ServiceSettingSnapshotRefreshResult
{
    private ServiceSettingSnapshotRefreshResult(
        ServiceSettingSnapshot? snapshot,
        bool activated,
        IReadOnlyList<ServiceSettingSnapshotError> errors)
    {
        Snapshot = snapshot;
        Activated = activated;
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether a complete valid snapshot was obtained.</summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>Gets the successful active snapshot, or null on failure.</summary>
    public ServiceSettingSnapshot? Snapshot { get; }

    /// <summary>Gets a value indicating whether this refresh replaced the active reference.</summary>
    public bool Activated { get; }

    /// <summary>Gets safe failures; the collection is empty on success.</summary>
    public IReadOnlyList<ServiceSettingSnapshotError> Errors { get; }

    internal static ServiceSettingSnapshotRefreshResult Success(
        ServiceSettingSnapshot snapshot,
        bool activated) => new(snapshot, activated, []);

    internal static ServiceSettingSnapshotRefreshResult Failure(
        params ServiceSettingSnapshotError[] errors) => new(null, false, Array.AsReadOnly(errors));

    internal static ServiceSettingSnapshotRefreshResult Failure(
        IEnumerable<ServiceSettingSnapshotError> errors) =>
        new(null, false, errors.ToList().AsReadOnly());

    /// <summary>Returns metadata only and never includes setting values.</summary>
    public override string ToString() =>
        $"ServiceSettingSnapshotRefreshResult(Succeeded={Succeeded}, Activated={Activated}, ErrorCount={Errors.Count})";
}

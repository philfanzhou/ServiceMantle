using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServiceMantle.Configuration;

/// <summary>
/// Loads, decrypts, validates, and atomically activates complete typed setting snapshots.
/// </summary>
/// <remarks>
/// Refreshes on one loader instance are serialized. Failures never replace an existing snapshot,
/// and caller cancellation is propagated instead of being converted to a validation result.
/// </remarks>
public sealed class ServiceSettingSnapshotLoader : IDisposable
{
    private const string SupportedEnvelopePrefix = "sm:v1:";
    private const string VersionedEnvelopePrefix = "sm:v";

    private readonly ServiceId serviceId;
    private readonly IServiceSettingSnapshotSource source;
    private readonly ServiceSettingDefinitionRegistry registry;
    private readonly ServiceSettingCurrentSnapshotAccessor accessor;
    private readonly IServiceSettingRootKeySource? rootKeySource;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    /// <summary>Initializes a setting snapshot loader.</summary>
    public ServiceSettingSnapshotLoader(
        ServiceId serviceId,
        IServiceSettingSnapshotSource source,
        ServiceSettingDefinitionRegistry registry,
        ServiceSettingCurrentSnapshotAccessor accessor,
        IServiceSettingRootKeySource? rootKeySource = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(accessor);
        this.serviceId = serviceId;
        this.source = source;
        this.registry = registry;
        this.accessor = accessor;
        this.rootKeySource = rootKeySource;
    }

    /// <summary>
    /// Refreshes the process-local snapshot and returns only safe failure classifications.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the refresh.</exception>
    public async ValueTask<ServiceSettingSnapshotRefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw SafeCancellation(cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceSettingSnapshotRead read;
            try
            {
                read = await source.LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCancellation(cancellationToken);
            }
            catch
            {
                return Failure(WellKnownServiceSettingSnapshotErrorCodes.LoadFailed);
            }

            if (read is null)
            {
                return Failure(WellKnownServiceSettingSnapshotErrorCodes.LoadFailed);
            }

            var materialized = await MaterializeAsync(read, cancellationToken).ConfigureAwait(false);
            if (!materialized.Succeeded)
            {
                return materialized.Result!;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var candidate = materialized.Snapshot!;
            if (accessor.TryGetCurrent(out var current))
            {
                if (candidate.Version < current!.Version)
                {
                    return Failure(WellKnownServiceSettingSnapshotErrorCodes.Stale);
                }

                if (candidate.Version == current.Version)
                {
                    return CryptographicOperations.FixedTimeEquals(
                            candidate.NormalizedFingerprint,
                            current.NormalizedFingerprint)
                        ? ServiceSettingSnapshotRefreshResult.Success(current, activated: false)
                        : Failure(WellKnownServiceSettingSnapshotErrorCodes.Conflict);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            accessor.Publish(candidate);
            return ServiceSettingSnapshotRefreshResult.Success(candidate, activated: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw SafeCancellation(cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => refreshLock.Dispose();

    private async ValueTask<MaterializationResult> MaterializeAsync(
        ServiceSettingSnapshotRead read,
        CancellationToken cancellationToken)
    {
        if (read.ServiceId != serviceId)
        {
            return MaterializationResult.Failure(
                Failure(WellKnownServiceSettingSnapshotErrorCodes.ServiceMismatch));
        }

        if (read.Version < 0 ||
            read.Values.Any(value => value is null || value.Version != read.Version) ||
            (read.Version == 0 && read.Values.Count != 0))
        {
            return MaterializationResult.Failure(
                Failure(WellKnownServiceSettingSnapshotErrorCodes.MixedVersion));
        }

        var rawValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? rootKey = null;
        foreach (var persisted in read.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!registry.TryGetDefinition(persisted.Key, out var definition))
            {
                return MaterializationResult.Failure(
                    Failure(WellKnownServiceSettingSnapshotErrorCodes.UnknownKey));
            }

            if (!rawValues.TryAdd(definition!.Key, null))
            {
                return MaterializationResult.Failure(Failure(
                    definition.Key,
                    WellKnownServiceSettingSnapshotErrorCodes.DuplicateKey));
            }

            if (!Enum.IsDefined(persisted.ValueType) || persisted.ValueType != definition.ValueType)
            {
                return MaterializationResult.Failure(Failure(
                    definition.Key,
                    WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch));
            }

            if (!definition.IsSensitive)
            {
                rawValues[definition.Key] = persisted.Value;
                continue;
            }

            if (!persisted.Value.StartsWith(SupportedEnvelopePrefix, StringComparison.Ordinal))
            {
                var code = persisted.Value.StartsWith(VersionedEnvelopePrefix, StringComparison.Ordinal)
                    ? WellKnownServiceSettingSnapshotErrorCodes.SensitiveVersionUnsupported
                    : WellKnownServiceSettingSnapshotErrorCodes.SensitiveEnvelopeRequired;
                return MaterializationResult.Failure(Failure(definition.Key, code));
            }

            if (rootKey is null)
            {
                if (rootKeySource is null)
                {
                    return MaterializationResult.Failure(Failure(
                        definition.Key,
                        WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable));
                }

                try
                {
                    rootKey = await rootKeySource.GetRootKeyAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(rootKey))
                    {
                        return MaterializationResult.Failure(Failure(
                            definition.Key,
                            WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw SafeCancellation(cancellationToken);
                }
                catch
                {
                    return MaterializationResult.Failure(Failure(
                        definition.Key,
                        WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable));
                }
            }

            try
            {
                rawValues[definition.Key] = new SensitiveValueProtector(serviceId, definition.Key)
                    .Unprotect(persisted.Value, rootKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw SafeCancellation(cancellationToken);
            }
            catch (SensitiveValueProtectionException exception)
            {
                var code = exception.ErrorCode switch
                {
                    WellKnownSensitiveValueProtectionErrorCodes.UnsupportedVersion =>
                        WellKnownServiceSettingSnapshotErrorCodes.SensitiveVersionUnsupported,
                    WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed =>
                        WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed,
                    _ => WellKnownServiceSettingSnapshotErrorCodes.SensitiveCiphertextInvalid,
                };
                return MaterializationResult.Failure(Failure(definition.Key, code));
            }
            catch
            {
                return MaterializationResult.Failure(Failure(
                    definition.Key,
                    WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ServiceSettingValidationResult validation;
        try
        {
            validation = registry.Validate(rawValues);
        }
        catch
        {
            return MaterializationResult.Failure(
                Failure(WellKnownServiceSettingSnapshotErrorCodes.ValidationFailed));
        }

        if (!validation.IsValid)
        {
            var errors = validation.Errors.Select(error => new ServiceSettingSnapshotError(
                error.Key,
                error.ErrorCode switch
                {
                    WellKnownServiceSettingValidationErrorCodes.Required =>
                        WellKnownServiceSettingSnapshotErrorCodes.MissingRequired,
                    WellKnownServiceSettingValidationErrorCodes.InvalidNumber or
                    WellKnownServiceSettingValidationErrorCodes.InvalidBoolean or
                    WellKnownServiceSettingValidationErrorCodes.InvalidJson =>
                        WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch,
                    _ => WellKnownServiceSettingSnapshotErrorCodes.ValidationFailed,
                }));
            return MaterializationResult.Failure(ServiceSettingSnapshotRefreshResult.Failure(errors));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = CreateFingerprint(validation.Values);
        return MaterializationResult.Success(new ServiceSettingSnapshot(
            serviceId,
            read.Version,
            validation.Values,
            fingerprint));
    }

    private static byte[] CreateFingerprint(
        IReadOnlyDictionary<string, ServiceSettingValue> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, pair.Key);
            hash.AppendData([(byte)pair.Value.ValueType]);
            hash.AppendData([pair.Value.HasValue ? (byte)1 : (byte)0]);
            hash.AppendData([pair.Value.IsDefault ? (byte)1 : (byte)0]);
            if (pair.Value.HasValue)
            {
                Append(hash, NormalizeValue(pair.Value));
            }
        }

        return hash.GetHashAndReset();
    }

    private static string NormalizeValue(ServiceSettingValue value) => value.ValueType switch
    {
        ServiceSettingValueType.String => value.GetString(),
        ServiceSettingValueType.Number => value.GetNumber().ToString("G29", CultureInfo.InvariantCulture),
        ServiceSettingValueType.Boolean => value.GetBoolean() ? "true" : "false",
        ServiceSettingValueType.Json => NormalizeJson(value.GetJson()),
        _ => throw new InvalidOperationException(),
    };

    private static string NormalizeJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static ServiceSettingSnapshotRefreshResult Failure(string errorCode) =>
        ServiceSettingSnapshotRefreshResult.Failure(new ServiceSettingSnapshotError(null, errorCode));

    private static ServiceSettingSnapshotRefreshResult Failure(string key, string errorCode) =>
        ServiceSettingSnapshotRefreshResult.Failure(new ServiceSettingSnapshotError(key, errorCode));

    private static OperationCanceledException SafeCancellation(CancellationToken cancellationToken) =>
        new("The service setting snapshot refresh was cancelled by the caller.", cancellationToken);

    private sealed record MaterializationResult(
        ServiceSettingSnapshot? Snapshot,
        ServiceSettingSnapshotRefreshResult? Result)
    {
        internal bool Succeeded => Snapshot is not null;

        internal static MaterializationResult Success(ServiceSettingSnapshot snapshot) =>
            new(snapshot, null);

        internal static MaterializationResult Failure(ServiceSettingSnapshotRefreshResult result) =>
            new(null, result);
    }
}

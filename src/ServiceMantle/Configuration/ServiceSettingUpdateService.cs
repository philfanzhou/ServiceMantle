using System.Globalization;
using ServiceMantle.Audit;

namespace ServiceMantle.Configuration;

/// <summary>Validates, protects and saves a complete setting batch with key-only audit records.</summary>
/// <remarks>
/// The caller owns authentication, transaction creation and final commit. This service never
/// publishes a runtime snapshot or retries a conflict. Product keys and validation codes must be
/// non-secret; unknown input keys and raw exception details are not returned.
/// </remarks>
public sealed class ServiceSettingUpdateService(
    ServiceId serviceId,
    ServiceSettingDefinitionRegistry registry,
    IServiceSettingUpdateTransaction transaction,
    IServiceSettingRootKeySource? rootKeySource = null,
    TimeProvider? timeProvider = null)
{
    private readonly ServiceId serviceId = serviceId ?? throw new ArgumentNullException(nameof(serviceId));
    private readonly ServiceSettingDefinitionRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IServiceSettingUpdateTransaction transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));

    /// <summary>Applies one batch within the caller's transaction, or returns a safe failure.</summary>
    /// <exception cref="OperationCanceledException">The caller requested cancellation.</exception>
    public async ValueTask<ServiceSettingUpdateResult> UpdateAsync(
        ServiceSettingUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<ServiceSettingValidationError>();
            foreach (var (key, value) in command.Changes)
            {
                if (!registry.TryGetDefinition(key, out var definition))
                {
                    errors.Add(new(null, WellKnownServiceSettingValidationErrorCodes.Unknown));
                }
                else if (!changes.TryAdd(definition!.Key, value))
                {
                    errors.Add(new(definition.Key, WellKnownServiceSettingValidationErrorCodes.Duplicate));
                }
            }
            if (errors.Count != 0)
            {
                return ServiceSettingUpdateResult.Invalid(errors);
            }

            var current = await transaction.LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (current.ServiceId != serviceId)
            {
                return Failure(ServiceSettingUpdateStatus.StorageFailed);
            }
            if (current.Version != command.ExpectedVersion)
            {
                return Failure(ServiceSettingUpdateStatus.VersionConflict);
            }
            if (current.Version == long.MaxValue)
            {
                return Failure(ServiceSettingUpdateStatus.VersionExhausted);
            }

            string? rootKey = null;
            async ValueTask<string> GetKeyAsync()
            {
                rootKey ??= rootKeySource is null ? null :
                    await rootKeySource.GetRootKeyAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return !string.IsNullOrWhiteSpace(rootKey) ? rootKey :
                    throw new InvalidOperationException("The setting protection key is unavailable.");
            }

            var candidate = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (key, value) in current.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!registry.TryGetDefinition(key, out var definition))
                    {
                        return Failure(ServiceSettingUpdateStatus.StorageFailed);
                    }
                    candidate.Add(definition!.Key, definition.IsSensitive
                        ? new SensitiveValueProtector(serviceId, definition.Key)
                            .Unprotect(value, await GetKeyAsync().ConfigureAwait(false), cancellationToken)
                        : value);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(ServiceSettingUpdateStatus.ProtectionFailed);
            }

            foreach (var (key, value) in changes)
            {
                if (value is null) candidate.Remove(key);
                else candidate[key] = value;
            }
            var validation = registry.Validate(candidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (!validation.IsValid)
            {
                return ServiceSettingUpdateResult.Invalid(validation.Errors.Select(error =>
                    new ServiceSettingValidationError(
                        error.Key is not null && registry.TryGetDefinition(error.Key, out var definition)
                            ? definition!.Key : null,
                        error.ErrorCode)));
            }

            var persisted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var restartRequired = current.RestartRequired;
            var audits = new List<ManagementAuditEvent>();
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            foreach (var (key, rawValue) in changes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = validation.Values[key];
                string? normalized = rawValue is null ? null : Normalize(value);
                if (normalized is not null && value.IsSensitive)
                {
                    try
                    {
                        normalized = new SensitiveValueProtector(serviceId, key)
                            .Protect(normalized, await GetKeyAsync().ConfigureAwait(false), cancellationToken);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        return Failure(ServiceSettingUpdateStatus.ProtectionFailed);
                    }
                }
                persisted.Add(key, normalized);
                restartRequired |= value.Definition.RequiresRestart;
                audits.Add(ManagementAuditEvent.Create(
                    command.Operator,
                    WellKnownManagementAuditActions.ConfigurationChanged,
                    ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, serviceId.Value),
                    ManagementAuditOutcome.Success,
                    occurredAtUtc: now,
                    metadata: new Dictionary<string, string> { ["key"] = key }));
            }

            var update = new ServiceSettingStoreUpdate(command.ExpectedVersion, persisted,
                command.Operator.OperatorId ?? "system", restartRequired);
            cancellationToken.ThrowIfCancellationRequested();
            return await transaction.ApplyAsync(serviceId, update, audits.AsReadOnly(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The setting batch was cancelled by the caller.", cancellationToken);
        }
        catch
        {
            return Failure(ServiceSettingUpdateStatus.StorageFailed);
        }
    }

    private static string Normalize(ServiceSettingValue value) => value.ValueType switch
    {
        ServiceSettingValueType.String => value.GetString(),
        ServiceSettingValueType.Number => value.GetNumber().ToString("G29", CultureInfo.InvariantCulture),
        ServiceSettingValueType.Boolean => value.GetBoolean() ? "true" : "false",
        ServiceSettingValueType.Json => System.Text.Json.JsonSerializer.Serialize(value.GetJson()),
        _ => throw new InvalidOperationException("The setting type is unsupported.")
    };

    private static ServiceSettingUpdateResult Failure(ServiceSettingUpdateStatus status) =>
        ServiceSettingUpdateResult.Failure(status);
}

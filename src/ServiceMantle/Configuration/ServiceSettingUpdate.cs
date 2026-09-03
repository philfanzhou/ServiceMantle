using System.Collections.ObjectModel;
using ServiceMantle.Audit;

namespace ServiceMantle.Configuration;

/// <summary>One immutable, versioned batch of plaintext setting changes.</summary>
public sealed class ServiceSettingUpdateCommand
{
    /// <summary>Initializes a batch of one to 32 changes. Null removes an explicit value.</summary>
    public ServiceSettingUpdateCommand(
        long expectedVersion,
        IReadOnlyDictionary<string, string?> changes,
        ManagementAuditOperator operatorInfo)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(operatorInfo);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (changes.Count is < 1 or > 32)
        {
            throw new ArgumentException("A setting batch must contain one to 32 changes.", nameof(changes));
        }

        ExpectedVersion = expectedVersion;
        Changes = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(changes));
        Operator = ManagementAuditOperator.Create(operatorInfo.Source, operatorInfo.OperatorId);
    }

    /// <summary>Gets the service-level version read by the caller.</summary>
    public long ExpectedVersion { get; }

    /// <summary>Gets the plaintext input. Never log or return this collection.</summary>
    public IReadOnlyDictionary<string, string?> Changes { get; }

    /// <summary>Gets the caller-validated, non-secret audit identity.</summary>
    public ManagementAuditOperator Operator { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ServiceSettingUpdateCommand(ExpectedVersion={ExpectedVersion}, ChangeCount={Changes.Count})";
}

/// <summary>Closed outcomes for a transactional setting update.</summary>
public enum ServiceSettingUpdateStatus
{
    /// <summary>The batch is saved inside the caller's transaction, which still requires commit.</summary>
    Applied,
    /// <summary>The complete candidate or its keys failed validation.</summary>
    ValidationFailed,
    /// <summary>The expected version no longer matches; retry in a fresh transaction after reading.</summary>
    VersionConflict,
    /// <summary>The version cannot be incremented.</summary>
    VersionExhausted,
    /// <summary>A sensitive value or external key could not be processed.</summary>
    ProtectionFailed,
    /// <summary>Storage failed. The caller must roll back and dispose its transaction on failure.</summary>
    StorageFailed,
    /// <summary>An explicit transaction supporting savepoints is required.</summary>
    TransactionRequired,
    /// <summary>The context contains pending work or a tracked setting aggregate.</summary>
    ContextNotClean
}

/// <summary>A value-free result. Applied is not a claim that the caller committed its transaction.</summary>
public sealed class ServiceSettingUpdateResult
{
    private ServiceSettingUpdateResult(
        ServiceSettingUpdateStatus status,
        long? version,
        IReadOnlyList<ServiceSettingValidationError> errors)
    {
        Status = status;
        Version = version;
        Errors = errors;
    }

    /// <summary>Gets the closed update outcome.</summary>
    public ServiceSettingUpdateStatus Status { get; }
    /// <summary>Gets whether the batch was saved within the caller's transaction.</summary>
    public bool Succeeded => Status == ServiceSettingUpdateStatus.Applied;
    /// <summary>Gets the staged version on success.</summary>
    public long? Version { get; }
    /// <summary>Gets registered-key and non-secret-code validation failures.</summary>
    public IReadOnlyList<ServiceSettingValidationError> Errors { get; }

    /// <summary>Creates an applied result.</summary>
    public static ServiceSettingUpdateResult Applied(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        return new(ServiceSettingUpdateStatus.Applied, version, []);
    }

    /// <summary>Creates a safe failure.</summary>
    public static ServiceSettingUpdateResult Failure(ServiceSettingUpdateStatus status)
    {
        if (!Enum.IsDefined(status) || status == ServiceSettingUpdateStatus.Applied)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return new(status, null, []);
    }

    internal static ServiceSettingUpdateResult Invalid(IEnumerable<ServiceSettingValidationError> errors) =>
        new(ServiceSettingUpdateStatus.ValidationFailed, null,
            errors.OrderBy(error => error.Key, StringComparer.Ordinal)
                .ThenBy(error => error.ErrorCode, StringComparer.Ordinal).ToList().AsReadOnly());

    /// <inheritdoc />
    public override string ToString() =>
        $"ServiceSettingUpdateResult(Status={Status}, Version={Version}, ErrorCount={Errors.Count})";
}

/// <summary>Participates in one caller-owned transaction for settings and their audit records.</summary>
/// <remarks>
/// Implementations never commit the caller's transaction. Apply saves only the supplied batch and
/// audits atomically within a savepoint, and restores that savepoint on failure or cancellation.
/// A failed rollback requires the caller to discard the entire transaction. Instances are scoped
/// to one unit of work and must not be used concurrently.
/// </remarks>
public interface IServiceSettingUpdateTransaction
{
    /// <summary>Reads raw persisted values in the same transaction used by ApplyAsync.</summary>
    ValueTask<ServiceSettingStoreSnapshot> LoadAsync(ServiceId serviceId, CancellationToken cancellationToken);

    /// <summary>Saves protected changes and value-free audits without committing the outer transaction.</summary>
    ValueTask<ServiceSettingUpdateResult> ApplyAsync(
        ServiceId serviceId,
        ServiceSettingStoreUpdate update,
        IReadOnlyList<ManagementAuditEvent> audits,
        CancellationToken cancellationToken);
}

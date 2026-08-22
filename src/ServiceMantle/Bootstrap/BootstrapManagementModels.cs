using ServiceMantle;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// Identifies the kind of bootstrap change that was completed.
/// </summary>
public enum BootstrapChangeOperation
{
    /// <summary>
    /// A bootstrap file was created for the first time.
    /// </summary>
    Create,

    /// <summary>
    /// An existing bootstrap file was replaced.
    /// </summary>
    Update
}

/// <summary>
/// Contains the safe, non-secret projection of the local bootstrap state.
/// </summary>
public sealed class BootstrapManagementStatus
{
    internal BootstrapManagementStatus(
        ServiceId serviceId,
        InstanceId instanceId,
        bool isConfigured,
        string? provider,
        string? serverVersion,
        bool connectionStringConfigured,
        bool masterKeyConfigured)
    {
        ServiceId = serviceId;
        InstanceId = instanceId;
        IsConfigured = isConfigured;
        Provider = provider;
        ServerVersion = serverVersion;
        ConnectionStringConfigured = connectionStringConfigured;
        MasterKeyConfigured = masterKeyConfigured;
    }

    /// <summary>
    /// Gets the service identifier for this bootstrap store.
    /// </summary>
    public ServiceId ServiceId { get; }

    /// <summary>
    /// Gets the instance identifier for the manager handling the operation.
    /// </summary>
    public InstanceId InstanceId { get; }

    /// <summary>
    /// Gets a value indicating whether a valid bootstrap file exists.
    /// </summary>
    public bool IsConfigured { get; }

    /// <summary>
    /// Gets the configured database provider, or null when no bootstrap is configured.
    /// </summary>
    public string? Provider { get; }

    /// <summary>
    /// Gets the configured database server version, or null when it was not supplied.
    /// </summary>
    public string? ServerVersion { get; }

    /// <summary>
    /// Gets a value indicating whether a connection string is configured.
    /// </summary>
    public bool ConnectionStringConfigured { get; }

    /// <summary>
    /// Gets a value indicating whether a master key is configured.
    /// </summary>
    public bool MasterKeyConfigured { get; }

    /// <summary>
    /// Returns safe status information without secret values.
    /// </summary>
    public override string ToString() =>
        $"BootstrapManagementStatus(ServiceId={ServiceId.Value}, " +
        $"InstanceId={InstanceId.Value}, IsConfigured={IsConfigured}, " +
        $"Provider={Provider ?? "<none>"}, ServerVersion={ServerVersion ?? "<none>"}, " +
        $"ConnectionStringConfigured={ConnectionStringConfigured}, " +
        $"MasterKeyConfigured={MasterKeyConfigured})";
}

/// <summary>
/// Contains the complete input required for first-time bootstrap creation.
/// </summary>
public sealed class BootstrapCreateRequest
{
    /// <summary>
    /// Initializes a bootstrap creation request.
    /// </summary>
    /// <param name="database">The complete database replacement.</param>
    /// <param name="masterKey">The master key to store.</param>
    public BootstrapCreateRequest(
        BootstrapDatabaseConfiguration database,
        string masterKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(masterKey);

        Database = database;
        MasterKey = masterKey;
    }

    /// <summary>
    /// Gets the complete database configuration to validate and create.
    /// </summary>
    public BootstrapDatabaseConfiguration Database { get; }

    /// <summary>
    /// Gets the master key to validate and create.
    /// </summary>
    public string MasterKey { get; }

    /// <summary>
    /// Returns safe request information without secret values.
    /// </summary>
    public override string ToString() =>
        $"BootstrapCreateRequest(Provider={Database.Provider}, MasterKeyProvided={!string.IsNullOrWhiteSpace(MasterKey)})";
}

/// <summary>
/// Contains optional replacement values for an existing bootstrap configuration.
/// </summary>
public sealed class BootstrapUpdateRequest
{
    /// <summary>
    /// Initializes a bootstrap update request.
    /// </summary>
    /// <param name="replacementDatabase">The complete database replacement, or null to retain it.</param>
    /// <param name="replacementMasterKey">The replacement master key, or null/whitespace to retain it.</param>
    public BootstrapUpdateRequest(
        BootstrapDatabaseConfiguration? replacementDatabase = null,
        string? replacementMasterKey = null)
    {
        ReplacementDatabase = replacementDatabase;
        ReplacementMasterKey = replacementMasterKey;
    }

    /// <summary>
    /// Gets the complete database replacement, or null when it should be retained.
    /// </summary>
    public BootstrapDatabaseConfiguration? ReplacementDatabase { get; }

    /// <summary>
    /// Gets the replacement master key, or null/whitespace when it should be retained.
    /// </summary>
    public string? ReplacementMasterKey { get; }

    /// <summary>
    /// Returns safe request information without secret values.
    /// </summary>
    public override string ToString() =>
        $"BootstrapUpdateRequest(Provider={ReplacementDatabase?.Provider ?? "<unchanged>"}, " +
        $"MasterKeyReplacementProvided={!string.IsNullOrWhiteSpace(ReplacementMasterKey)})";
}

/// <summary>
/// Describes a completed bootstrap change without returning configuration secrets.
/// </summary>
public sealed class BootstrapChangeResult
{
    internal BootstrapChangeResult(
        ServiceId serviceId,
        InstanceId instanceId,
        BootstrapChangeOperation operation)
    {
        ServiceId = serviceId;
        InstanceId = instanceId;
        Operation = operation;
    }

    /// <summary>
    /// Gets the service identifier affected by the change.
    /// </summary>
    public ServiceId ServiceId { get; }

    /// <summary>
    /// Gets the instance identifier that performed the change.
    /// </summary>
    public InstanceId InstanceId { get; }

    /// <summary>
    /// Gets the operation that was completed.
    /// </summary>
    public BootstrapChangeOperation Operation { get; }

    /// <summary>
    /// Gets a value indicating whether the process must restart to activate the change.
    /// </summary>
    public bool RestartRequired => true;

    /// <summary>
    /// Returns safe result information without secret values.
    /// </summary>
    public override string ToString() =>
        $"BootstrapChangeResult(ServiceId={ServiceId.Value}, InstanceId={InstanceId.Value}, " +
        $"Operation={Operation}, RestartRequired={RestartRequired})";
}

/// <summary>
/// Represents the structured result of validating a bootstrap candidate.
/// </summary>
public sealed class BootstrapValidationResult
{
    private BootstrapValidationResult(bool isValid, string? errorCode)
    {
        IsValid = isValid;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a value indicating whether the candidate passed validation.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Gets the safe validation error code, or null when validation succeeded.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static BootstrapValidationResult Success() => new(true, null);

    /// <summary>
    /// Creates a failed validation result with a safe error code.
    /// </summary>
    /// <param name="errorCode">An ASCII error code containing only letters, digits, dot, dash, or underscore.</param>
    /// <exception cref="ArgumentException">The error code is not safe or is empty.</exception>
    public static BootstrapValidationResult Failure(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (errorCode.Length > 64)
        {
            throw new ArgumentException("The validation error code is too long.", nameof(errorCode));
        }

        foreach (var character in errorCode)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                throw new ArgumentException(
                    "The validation error code contains an invalid character.",
                    nameof(errorCode));
            }
        }

        return new BootstrapValidationResult(false, errorCode);
    }

    /// <summary>
    /// Returns the structured result without secret values.
    /// </summary>
    public override string ToString() =>
        IsValid
            ? "BootstrapValidationResult(IsValid=True)"
            : $"BootstrapValidationResult(IsValid=False, ErrorCode={ErrorCode})";

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

/// <summary>
/// Validates a complete bootstrap candidate before it is persisted.
/// </summary>
public interface IBootstrapCandidateValidator
{
    /// <summary>
    /// Validates a candidate without persisting it.
    /// </summary>
    /// <param name="candidate">The complete candidate, available only inside the validation boundary.</param>
    /// <param name="cancellationToken">The cancellation token for the validation operation.</param>
    /// <returns>A structured success or safe failure result.</returns>
    ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapConfiguration candidate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Indicates that a bootstrap management operation was rejected safely.
/// </summary>
public sealed class BootstrapManagementException : Exception
{
    internal BootstrapManagementException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the safe error code for the management failure.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Returns only safe management error information.
    /// </summary>
    public override string ToString() =>
        $"BootstrapManagementException(ErrorCode={ErrorCode}, Message={Message})";
}

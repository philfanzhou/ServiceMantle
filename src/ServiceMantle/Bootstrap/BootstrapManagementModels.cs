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
/// Common provider identifiers used by bootstrap configuration metadata.
/// </summary>
public static class WellKnownDatabaseProviderIds
{
    /// <summary>
    /// PostgreSQL provider id.
    /// </summary>
    public const string PostgreSql = "PostgreSQL";

    /// <summary>
    /// SQLite provider id.
    /// </summary>
    public const string Sqlite = "SQLite";

    /// <summary>
    /// MySQL provider id.
    /// </summary>
    public const string MySql = "MySQL";

    /// <summary>
    /// MariaDB provider id.
    /// </summary>
    public const string MariaDb = "MariaDB";

    /// <summary>
    /// Oracle provider id.
    /// </summary>
    public const string Oracle = "Oracle";

    /// <summary>
    /// SQL Server provider id.
    /// </summary>
    public const string SqlServer = "SqlServer";
}

/// <summary>
/// Describes how a provider is expected to persist bootstrap targets.
/// </summary>
public enum BootstrapDatabaseTargetKind
{
    /// <summary>
    /// The provider uses a server-hosted database.
    /// </summary>
    ServerDatabase,

    /// <summary>
    /// The provider uses a local file database.
    /// </summary>
    File,

    /// <summary>
    /// The provider manages a database server schema.
    /// </summary>
    ServerSchema
}

/// <summary>
/// Describes server-version requirements for a provider.
/// </summary>
public enum BootstrapServerVersionRequirement
{
    /// <summary>
    /// A server version is required by the provider.
    /// </summary>
    Required,

    /// <summary>
    /// A server version is optional.
    /// </summary>
    Optional,

    /// <summary>
    /// A server version must be omitted for this provider.
    /// </summary>
    Forbidden
}

/// <summary>
/// Describes a provider implementation without exposing any secret configuration data.
/// </summary>
public sealed class BootstrapDatabaseProviderDescriptor
{
    /// <summary>
    /// Initializes a provider descriptor.
    /// </summary>
    public BootstrapDatabaseProviderDescriptor(
        string id,
        string displayName,
        BootstrapDatabaseTargetKind targetKind,
        BootstrapServerVersionRequirement serverVersionRequirement,
        IEnumerable<string>? aliases = null)
    {
        Id = DatabaseProviderId.Normalize(id, nameof(id));

        DisplayName = displayName?.Trim() ??
            throw new ArgumentNullException(nameof(displayName));

        if (DisplayName.Length == 0)
        {
            throw new ArgumentException(
                "The provider display name cannot be empty.",
                nameof(displayName));
        }

        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }

        if (!Enum.IsDefined(serverVersionRequirement))
        {
            throw new ArgumentOutOfRangeException(nameof(serverVersionRequirement));
        }

        TargetKind = targetKind;
        ServerVersionRequirement = serverVersionRequirement;
        Aliases = NormalizeAliases(aliases);
    }

    /// <summary>
    /// Gets the provider identifier used in bootstrap files.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the bootstrap target strategy for this provider.
    /// </summary>
    public BootstrapDatabaseTargetKind TargetKind { get; }

    /// <summary>
    /// Gets the server-version requirement for this provider.
    /// </summary>
    public BootstrapServerVersionRequirement ServerVersionRequirement { get; }

    /// <summary>
    /// Gets a copy of any aliases for this provider.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// Returns safe provider metadata.
    /// </summary>
    public override string ToString() =>
        $"BootstrapDatabaseProviderDescriptor(Id={Id}, DisplayName={DisplayName}, " +
        $"TargetKind={TargetKind}, ServerVersionRequirement={ServerVersionRequirement}, " +
        $"Aliases={string.Join(',', Aliases)})";

    private static IReadOnlyList<string> NormalizeAliases(IEnumerable<string>? aliases)
    {
        if (aliases is null)
        {
            return [];
        }

        var normalizedAliases = new List<string>();

        foreach (var alias in aliases)
        {
            if (alias is null)
            {
                throw new ArgumentNullException(nameof(aliases));
            }

            normalizedAliases.Add(DatabaseProviderId.Normalize(alias, nameof(aliases)));
        }

        return normalizedAliases.AsReadOnly();
    }
}

/// <summary>
/// Represents a concrete provider-specific bootstrap validator.
/// </summary>
public interface IBootstrapDatabaseProvider
{
    /// <summary>
    /// Gets provider metadata used by administration workflows.
    /// </summary>
    BootstrapDatabaseProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Validates whether the database configuration can be accepted for this provider.
    /// </summary>
    /// <param name="database">The database candidate.</param>
    /// <param name="cancellationToken">Cancellation token for the validation call.</param>
    ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves and holds a read-only bootstrap provider registration table.
/// </summary>
public sealed class BootstrapDatabaseProviderRegistry
{
    private readonly IReadOnlyList<BootstrapDatabaseProviderDescriptor> descriptors;
    private readonly Dictionary<string, ProviderRegistration> registrations;

    /// <summary>
    /// Initializes a provider registry.
    /// </summary>
    /// <param name="providers">All providers to expose through this registry.</param>
    public BootstrapDatabaseProviderRegistry(IEnumerable<IBootstrapDatabaseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        this.registrations = new Dictionary<string, ProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        var descriptorList = new List<BootstrapDatabaseProviderDescriptor>();

        foreach (var provider in providers)
        {
            if (provider is null)
            {
                throw new ArgumentNullException(nameof(providers));
            }

            var descriptor = provider.Descriptor;
            if (descriptor is null)
            {
                throw new ArgumentException("Provider descriptor cannot be null.", nameof(providers));
            }

            if (this.registrations.ContainsKey(descriptor.Id))
            {
                throw new ArgumentException(
                    $"The provider id '{descriptor.Id}' is already registered.",
                    nameof(providers));
            }

            var registration = new ProviderRegistration(
                provider,
                descriptor);

            this.registrations.Add(descriptor.Id, registration);

            foreach (var alias in descriptor.Aliases)
            {
                if (this.registrations.ContainsKey(alias))
                {
                    throw new ArgumentException(
                        $"The alias '{alias}' conflicts with a registered canonical id.",
                        nameof(providers));
                }

                this.registrations.Add(alias, registration);
            }

            descriptorList.Add(descriptor);
        }

        this.descriptors = descriptorList
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets provider descriptors in deterministic order.
    /// </summary>
    public IReadOnlyList<BootstrapDatabaseProviderDescriptor> Descriptors => descriptors;

    /// <summary>
    /// Looks up a provider by canonical id or alias.
    /// </summary>
    /// <param name="providerId">The provider id or alias.</param>
    /// <param name="provider">The provider instance when found.</param>
    public bool TryGetProvider(string providerId, out IBootstrapDatabaseProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            provider = null;
            return false;
        }

        if (!registrations.TryGetValue(providerId, out var registration))
        {
            provider = null;
            return false;
        }

        provider = registration.Provider;
        return true;
    }

    /// <summary>
    /// Resolves a canonical provider id from either that id or one of its aliases.
    /// </summary>
    /// <param name="providerId">The provider id or alias.</param>
    /// <param name="canonicalProviderId">The registered canonical id when found.</param>
    public bool TryGetCanonicalProviderId(
        string providerId,
        out string? canonicalProviderId)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            !registrations.TryGetValue(providerId, out var registration))
        {
            canonicalProviderId = null;
            return false;
        }

        canonicalProviderId = registration.Descriptor.Id;
        return true;
    }

    internal bool TryGetRegistration(
        string providerId,
        out ProviderRegistration? registration)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            registration = null;
            return false;
        }

        return registrations.TryGetValue(providerId, out registration);
    }

    internal sealed class ProviderRegistration
    {
        public ProviderRegistration(
            IBootstrapDatabaseProvider provider,
            BootstrapDatabaseProviderDescriptor descriptor)
        {
            Provider = provider;
            Descriptor = descriptor;
        }

        public IBootstrapDatabaseProvider Provider { get; }
        public BootstrapDatabaseProviderDescriptor Descriptor { get; }
    }
}

/// <summary>
/// Validates bootstrap candidates by dispatching to a registered provider.
/// </summary>
public sealed class BootstrapDatabaseCandidateValidator : IBootstrapCandidateValidator
{
    private readonly BootstrapDatabaseProviderRegistry providerRegistry;

    /// <summary>
    /// Initializes a candidate validator.
    /// </summary>
    /// <param name="providerRegistry">The bootstrap provider registry.</param>
    public BootstrapDatabaseCandidateValidator(BootstrapDatabaseProviderRegistry providerRegistry)
    {
        this.providerRegistry = providerRegistry ??
            throw new ArgumentNullException(nameof(providerRegistry));
    }

    /// <summary>
    /// Validates a candidate by resolving the provider and applying shared checks.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapConfiguration candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!providerRegistry.TryGetRegistration(candidate.Database.Provider, out var registration) ||
            registration is null)
        {
            return BootstrapValidationResult.Failure("database.provider_not_registered");
        }

        var descriptor = registration.Descriptor;
        var serverVersion = candidate.Database.ServerVersion;

        if (descriptor.ServerVersionRequirement == BootstrapServerVersionRequirement.Required &&
            string.IsNullOrWhiteSpace(serverVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_required");
        }

        if (descriptor.ServerVersionRequirement == BootstrapServerVersionRequirement.Forbidden &&
            !string.IsNullOrWhiteSpace(serverVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_not_allowed");
        }

        try
        {
            var database = string.Equals(
                candidate.Database.Provider,
                descriptor.Id,
                StringComparison.OrdinalIgnoreCase)
                ? candidate.Database
                : new BootstrapDatabaseConfiguration(
                    descriptor.Id,
                    candidate.Database.ServerVersion,
                    candidate.Database.ConnectionString);

            var result = await registration.Provider.ValidateAsync(database, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                return BootstrapValidationResult.Failure("database.provider_invalid_result");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BootstrapValidationResult.Failure("database.provider_validation_failed");
        }
    }
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

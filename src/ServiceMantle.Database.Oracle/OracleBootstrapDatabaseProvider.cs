using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.Oracle;

/// <summary>
/// Validates Oracle 19c-or-later single-instance PDB bootstrap targets without modifying them.
/// </summary>
public sealed class OracleBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private readonly IOracleDatabaseOperations operations;

    /// <summary>Initializes the provider with real ODP.NET probes.</summary>
    public OracleBootstrapDatabaseProvider()
        : this(new OracleDatabaseOperations())
    {
    }

    internal OracleBootstrapDatabaseProvider(IOracleDatabaseOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    /// <summary>Gets the Oracle server-schema descriptor.</summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
        WellKnownDatabaseProviderIds.Oracle,
        "Oracle",
        BootstrapDatabaseTargetKind.ServerSchema,
        BootstrapServerVersionRequirement.Required);

    /// <summary>Validates version, direct password identity, supported topology, and connectivity.</summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                database.Provider,
                WellKnownDatabaseProviderIds.Oracle,
                StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapValidationResult.Failure("database.provider_mismatch");
        }

        if (!OracleDatabaseTarget.TryNormalizeServerVersion(database.ServerVersion, out var majorVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_invalid");
        }

        if (majorVersion < OracleDatabaseTarget.MinimumSupportedServerMajorVersion)
        {
            return BootstrapValidationResult.Failure("database.server_version_unsupported");
        }

        if (!OracleDatabaseTarget.TryBuildConnectionString(database.ConnectionString, out var builder) ||
            !OracleDatabaseTarget.TryGetTargetIdentity(builder, out var userName, out _, out _))
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }

        OracleDatabaseTarget.ApplySafeTimeout(builder);
        try
        {
            var outcome = await operations.ProbeTargetAsync(builder, userName, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return outcome switch
            {
                OracleTargetProbeOutcome.Success => BootstrapValidationResult.Success(),
                OracleTargetProbeOutcome.IdentityMismatch or
                OracleTargetProbeOutcome.UnsupportedTopology =>
                    BootstrapValidationResult.Failure("database.connection_string_invalid"),
                OracleTargetProbeOutcome.TopologyPermissionDenied or
                OracleTargetProbeOutcome.CreateSessionDenied =>
                    BootstrapValidationResult.Failure("database.permission_denied"),
                OracleTargetProbeOutcome.AccountLocked or
                OracleTargetProbeOutcome.PasswordExpired or
                OracleTargetProbeOutcome.InvalidCredentials =>
                    BootstrapValidationResult.Failure("database.authentication_failed"),
                OracleTargetProbeOutcome.ConnectionFailed =>
                    BootstrapValidationResult.Failure("database.connection_failed"),
                _ => BootstrapValidationResult.Failure("database.provider_validation_failed")
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Oracle bootstrap validation was cancelled by the caller.",
                cancellationToken);
        }
        catch (Exception)
        {
            return BootstrapValidationResult.Failure("database.provider_validation_failed");
        }
    }
}

using System.Globalization;
using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.MySql;

/// <summary>
/// Validates MySQL bootstrap candidates without modifying the configured database target.
/// </summary>
public sealed class MySqlBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private const int MinimumSupportedServerMajorVersion = 8;
    private readonly IMySqlBootstrapProbe probe;

    /// <summary>
    /// Initializes the MySQL provider with the real connection probe.
    /// </summary>
    public MySqlBootstrapDatabaseProvider()
        : this(new MySqlBootstrapProbe())
    {
    }

    internal MySqlBootstrapDatabaseProvider(IMySqlBootstrapProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        this.probe = probe;
    }

    /// <summary>
    /// Gets the logically independent MySQL provider descriptor.
    /// </summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
        WellKnownDatabaseProviderIds.MySql,
        "MySQL",
        BootstrapDatabaseTargetKind.ServerDatabase,
        BootstrapServerVersionRequirement.Required);

    /// <summary>
    /// Validates the declared MySQL version, connection settings, and target connectivity.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                database.Provider,
                WellKnownDatabaseProviderIds.MySql,
                StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapValidationResult.Failure("database.provider_mismatch");
        }

        if (!TryNormalizeServerVersion(database.ServerVersion, out var serverMajorVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_invalid");
        }

        if (serverMajorVersion < MinimumSupportedServerMajorVersion)
        {
            return BootstrapValidationResult.Failure("database.server_version_unsupported");
        }

        if (!MySqlDatabaseTarget.TryBuildConnectionString(database.ConnectionString, out var builder))
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }

        if (!MySqlDatabaseTarget.TryGetValidDatabaseName(builder, out _))
        {
            return BootstrapValidationResult.Failure("database.database_required");
        }

        MySqlDatabaseTarget.ApplySafeTimeouts(builder);

        try
        {
            var outcome = await probe.ProbeAsync(
                    builder,
                    (int)MySqlDatabaseTarget.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                MySqlProbeOutcome.Success => BootstrapValidationResult.Success(),
                MySqlProbeOutcome.TargetIdentityMismatch =>
                    BootstrapValidationResult.Failure("database.connection_string_invalid"),
                MySqlProbeOutcome.DatabaseNotFound =>
                    BootstrapValidationResult.Failure("database.target_not_found"),
                MySqlProbeOutcome.AuthenticationFailed =>
                    BootstrapValidationResult.Failure("database.authentication_failed"),
                MySqlProbeOutcome.TargetAccessDenied =>
                    BootstrapValidationResult.Failure("database.permission_denied"),
                MySqlProbeOutcome.ConnectionFailed =>
                    BootstrapValidationResult.Failure("database.connection_failed"),
                _ => BootstrapValidationResult.Failure("database.provider_validation_failed")
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreateSafeCancellationException(cancellationToken);
        }
        catch (Exception)
        {
            return BootstrapValidationResult.Failure("database.provider_validation_failed");
        }
    }

    private static bool TryNormalizeServerVersion(string? serverVersion, out int majorVersion)
    {
        majorVersion = 0;
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        var parts = serverVersion.Trim().Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out majorVersion);
    }

    private static OperationCanceledException CreateSafeCancellationException(
        CancellationToken cancellationToken) =>
        new("MySQL bootstrap validation was cancelled by the caller.", cancellationToken);
}

internal interface IMySqlBootstrapProbe
{
    ValueTask<MySqlProbeOutcome> ProbeAsync(
        MySqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal enum MySqlProbeOutcome
{
    Success,
    TargetIdentityMismatch,
    DatabaseNotFound,
    AuthenticationFailed,
    TargetAccessDenied,
    ConnectionFailed,
    ValidationFailed
}

internal sealed class MySqlBootstrapProbe : IMySqlBootstrapProbe
{
    public async ValueTask<MySqlProbeOutcome> ProbeAsync(
        MySqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT @@lower_case_table_names, " +
                "BINARY DATABASE() = BINARY @databaseName, " +
                "LOWER(DATABASE()) = LOWER(@databaseName)";
            command.Parameters.AddWithValue("@databaseName", connectionString.Database);
            command.CommandTimeout = commandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return MySqlProbeOutcome.ValidationFailed;
            }

            var lowerCaseTableNames = reader.GetInt32(0);
            var exactMatch = !reader.IsDBNull(1) && reader.GetBoolean(1);
            var caseFoldedMatch = !reader.IsDBNull(2) && reader.GetBoolean(2);
            return ResolveTargetIdentityOutcome(exactMatch, caseFoldedMatch, lowerCaseTableNames);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "MySQL database probe was cancelled.",
                    cancellationToken);
            }

            return MySqlProbeFailureClassifier.Classify(exception);
        }
    }

    internal static MySqlProbeOutcome ResolveTargetIdentityOutcome(
        bool exactMatch,
        bool caseFoldedMatch,
        int lowerCaseTableNames) =>
        MySqlDatabaseTarget.MatchesDatabaseIdentifierRules(
            exactMatch,
            caseFoldedMatch,
            lowerCaseTableNames)
            ? MySqlProbeOutcome.Success
            : MySqlProbeOutcome.TargetIdentityMismatch;
}

internal static class MySqlProbeFailureClassifier
{
    internal static MySqlProbeOutcome Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mySqlException)
            {
                var classified = Classify(mySqlException.ErrorCode);
                if (classified != MySqlProbeOutcome.ValidationFailed)
                {
                    return classified;
                }
            }

            if (current is SocketException or IOException or TimeoutException)
            {
                return MySqlProbeOutcome.ConnectionFailed;
            }
        }

        return MySqlProbeOutcome.ValidationFailed;
    }

    internal static MySqlProbeOutcome Classify(MySqlErrorCode errorCode) => errorCode switch
    {
        MySqlErrorCode.UnknownDatabase => MySqlProbeOutcome.DatabaseNotFound,
        MySqlErrorCode.AccessDenied => MySqlProbeOutcome.AuthenticationFailed,
        MySqlErrorCode.DatabaseAccessDenied => MySqlProbeOutcome.TargetAccessDenied,
        MySqlErrorCode.UnableToConnectToHost or
        MySqlErrorCode.ConnectionCountError or
        MySqlErrorCode.TooManyUserConnections or
        MySqlErrorCode.AbortingConnection or
        MySqlErrorCode.NewAbortingConnection or
        MySqlErrorCode.NetReadInterrupted or
        MySqlErrorCode.NetWriteInterrupted or
        MySqlErrorCode.CommandTimeoutExpired or
        MySqlErrorCode.QueryTimeout or
        MySqlErrorCode.ClientInteractionTimeout => MySqlProbeOutcome.ConnectionFailed,
        _ => MySqlProbeOutcome.ValidationFailed
    };
}

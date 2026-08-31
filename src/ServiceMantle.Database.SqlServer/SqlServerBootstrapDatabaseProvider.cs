using System.Data;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.SqlServer;

/// <summary>
/// Validates SQL Server bootstrap candidates without modifying the configured database target.
/// </summary>
public sealed class SqlServerBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private readonly ISqlServerTargetObservationProbe probe;

    /// <summary>
    /// Initializes the SQL Server provider with the real read-only probe.
    /// </summary>
    public SqlServerBootstrapDatabaseProvider()
        : this(new SqlServerTargetObservationProbe())
    {
    }

    internal SqlServerBootstrapDatabaseProvider(ISqlServerTargetObservationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        this.probe = probe;
    }

    /// <summary>
    /// Gets the SQL Server provider descriptor.
    /// </summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
        WellKnownDatabaseProviderIds.SqlServer,
        "SQL Server",
        BootstrapDatabaseTargetKind.ServerDatabase,
        BootstrapServerVersionRequirement.Required);

    /// <summary>
    /// Validates the declared SQL Server version, connection settings, server version, and target.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSqlServerProvider(database.Provider))
        {
            return BootstrapValidationResult.Failure("database.provider_mismatch");
        }

        if (!TryNormalizeServerVersion(database.ServerVersion, out var serverMajorVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_invalid");
        }

        if (serverMajorVersion < SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion)
        {
            return BootstrapValidationResult.Failure("database.server_version_unsupported");
        }

        if (!SqlServerDatabaseTarget.TryBuildConnectionString(database.ConnectionString, out var builder))
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }

        if (!SqlServerDatabaseTarget.TryGetValidDatabaseName(builder, out _))
        {
            return BootstrapValidationResult.Failure("database.database_required");
        }

        SqlServerDatabaseTarget.ApplySafeTimeouts(builder);

        try
        {
            var outcome = await probe.ObserveAsync(builder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                SqlServerObservationOutcome.Success => BootstrapValidationResult.Success(),
                SqlServerObservationOutcome.TargetIdentityMismatch =>
                    BootstrapValidationResult.Failure("database.connection_string_invalid"),
                SqlServerObservationOutcome.ServerVersionUnsupported =>
                    BootstrapValidationResult.Failure("database.server_version_unsupported"),
                SqlServerObservationOutcome.TargetMissing =>
                    BootstrapValidationResult.Failure("database.target_not_found"),
                SqlServerObservationOutcome.TargetAccessDeniedUnknown or
                SqlServerObservationOutcome.TargetAccessDeniedExisting =>
                    BootstrapValidationResult.Failure("database.permission_denied"),
                SqlServerObservationOutcome.TargetUnavailableExisting or
                SqlServerObservationOutcome.ConnectionFailed =>
                    BootstrapValidationResult.Failure("database.connection_failed"),
                SqlServerObservationOutcome.AuthenticationFailed =>
                    BootstrapValidationResult.Failure("database.authentication_failed"),
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

    private static bool IsSqlServerProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.SqlServer, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeServerVersion(string? serverVersion, out int majorVersion)
    {
        majorVersion = 0;
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        var parts = serverVersion.Trim().Split('.');
        if (parts.Length is < 1 or > 4)
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
        new("SQL Server bootstrap validation was cancelled by the caller.", cancellationToken);
}

internal interface ISqlServerTargetObservationProbe
{
    ValueTask<SqlServerObservationOutcome> ObserveAsync(
        SqlConnectionStringBuilder connectionString,
        CancellationToken cancellationToken);
}

internal enum SqlServerObservationOutcome
{
    Success,
    TargetIdentityMismatch,
    ServerVersionUnsupported,
    TargetMissing,
    TargetAccessDeniedUnknown,
    TargetAccessDeniedExisting,
    TargetUnavailableExisting,
    AuthenticationFailed,
    ConnectionFailed,
    ValidationFailed
}

internal sealed class SqlServerTargetObservationProbe : ISqlServerTargetObservationProbe
{
    public async ValueTask<SqlServerObservationOutcome> ObserveAsync(
        SqlConnectionStringBuilder connectionString,
        CancellationToken cancellationToken)
    {
        var directOutcome = await ProbeTargetAsync(connectionString, cancellationToken).ConfigureAwait(false);
        if (directOutcome != SqlServerObservationOutcome.TargetAccessDeniedUnknown)
        {
            return directOutcome;
        }

        return await InspectTargetFromMasterAsync(connectionString, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SqlServerObservationOutcome> ProbeTargetAsync(
        SqlConnectionStringBuilder connectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), DB_NAME()";
            command.CommandTimeout = SqlServerDatabaseTarget.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.IsDBNull(0) ||
                reader.IsDBNull(1))
            {
                return SqlServerObservationOutcome.ValidationFailed;
            }

            if (reader.GetInt32(0) < SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion)
            {
                return SqlServerObservationOutcome.ServerVersionUnsupported;
            }

            return string.Equals(reader.GetString(1), connectionString.InitialCatalog, StringComparison.Ordinal)
                ? SqlServerObservationOutcome.Success
                : SqlServerObservationOutcome.TargetIdentityMismatch;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "SQL Server target probe was cancelled.",
                    cancellationToken);
            }

            return SqlServerProbeFailureClassifier.Classify(exception);
        }
    }

    private static async ValueTask<SqlServerObservationOutcome> InspectTargetFromMasterAsync(
        SqlConnectionStringBuilder targetConnectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            var databaseName = targetConnectionString.InitialCatalog;
            var masterBuilder = new SqlConnectionStringBuilder(targetConnectionString.ConnectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
                Enlist = false
            };
            SqlServerDatabaseTarget.ApplySafeTimeouts(masterBuilder);

            await using var connection = new SqlConnection(masterBuilder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'),
                    HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY DATABASE'),
                    candidate.[name],
                    candidate.[state],
                    HAS_DBACCESS(candidate.[name])
                FROM (VALUES (0)) AS singleton(value)
                OUTER APPLY
                (
                    SELECT TOP (1) [name], [state]
                    FROM sys.databases
                    WHERE [name] = @databaseName
                    ORDER BY CASE
                        WHEN [name] COLLATE Latin1_General_100_BIN2 =
                             @databaseName COLLATE Latin1_General_100_BIN2 THEN 0
                        ELSE 1
                    END
                ) AS candidate;
                """;
            command.CommandTimeout = SqlServerDatabaseTarget.CommandTimeoutSeconds;
            command.Parameters.Add("@databaseName", SqlDbType.NVarChar, 123).Value = databaseName;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
            {
                return SqlServerObservationOutcome.ValidationFailed;
            }

            if (reader.GetInt32(0) < SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion)
            {
                return SqlServerObservationOutcome.ServerVersionUnsupported;
            }

            return InterpretMetadata(
                databaseName,
                (!reader.IsDBNull(1) && reader.GetInt32(1) == 1) ||
                (!reader.IsDBNull(2) && reader.GetInt32(2) == 1),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetByte(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5));
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "SQL Server metadata probe was cancelled.",
                    cancellationToken);
            }

            var classified = SqlServerProbeFailureClassifier.Classify(exception);
            return classified == SqlServerObservationOutcome.TargetAccessDeniedUnknown
                ? SqlServerObservationOutcome.TargetAccessDeniedUnknown
                : classified;
        }
    }

    internal static SqlServerObservationOutcome InterpretMetadata(
        string requestedDatabaseName,
        bool hasCompleteVisibility,
        string? visibleDatabaseName,
        byte? databaseState,
        int? hasDatabaseAccess)
    {
        if (visibleDatabaseName is null)
        {
            return hasCompleteVisibility
                ? SqlServerObservationOutcome.TargetMissing
                : SqlServerObservationOutcome.TargetAccessDeniedUnknown;
        }

        if (!string.Equals(visibleDatabaseName, requestedDatabaseName, StringComparison.Ordinal))
        {
            return SqlServerObservationOutcome.TargetIdentityMismatch;
        }

        if (databaseState != 0)
        {
            return SqlServerObservationOutcome.TargetUnavailableExisting;
        }

        return hasDatabaseAccess == 1
            ? SqlServerObservationOutcome.TargetUnavailableExisting
            : SqlServerObservationOutcome.TargetAccessDeniedExisting;
    }
}

internal static class SqlServerProbeFailureClassifier
{
    internal static SqlServerObservationOutcome Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                var targetUnavailable = false;
                var authenticationFailed = false;
                var connectionFailed = false;
                foreach (SqlError error in sqlException.Errors)
                {
                    var classified = Classify(error.Number);
                    targetUnavailable |= classified == SqlServerObservationOutcome.TargetAccessDeniedUnknown;
                    authenticationFailed |= classified == SqlServerObservationOutcome.AuthenticationFailed;
                    connectionFailed |= classified == SqlServerObservationOutcome.ConnectionFailed;
                }

                // SQL Server can send 4060 and 18456 in the same login response when credentials
                // are valid but the requested database cannot be opened. Preserve the more
                // specific target outcome so the read-only master fallback can establish whether
                // the target is missing or hidden.
                if (targetUnavailable)
                {
                    return SqlServerObservationOutcome.TargetAccessDeniedUnknown;
                }

                if (authenticationFailed)
                {
                    return SqlServerObservationOutcome.AuthenticationFailed;
                }

                if (connectionFailed)
                {
                    return SqlServerObservationOutcome.ConnectionFailed;
                }
            }

            if (current is SocketException or IOException or TimeoutException)
            {
                return SqlServerObservationOutcome.ConnectionFailed;
            }
        }

        return SqlServerObservationOutcome.ValidationFailed;
    }

    internal static SqlServerObservationOutcome Classify(int errorNumber) => errorNumber switch
    {
        4060 or 916 => SqlServerObservationOutcome.TargetAccessDeniedUnknown,
        18452 or 18456 => SqlServerObservationOutcome.AuthenticationFailed,
        -2 or 20 or 53 or 64 or 233 or 258 or 10053 or 10054 or 10060 or 10061 or 11001 =>
            SqlServerObservationOutcome.ConnectionFailed,
        _ => SqlServerObservationOutcome.ValidationFailed
    };
}

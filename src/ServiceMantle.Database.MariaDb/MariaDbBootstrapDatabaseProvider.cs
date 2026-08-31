using System.Globalization;
using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.MariaDb;

/// <summary>
/// Validates MariaDB bootstrap candidates without modifying the configured database target.
/// </summary>
public sealed class MariaDbBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private const int MinimumSupportedServerMajorVersion = 10;
    private const int MinimumSupportedServerMinorVersion = 11;
    private readonly IMariaDbBootstrapProbe probe;

    /// <summary>
    /// Initializes the MariaDB provider with the real connection probe.
    /// </summary>
    public MariaDbBootstrapDatabaseProvider()
        : this(new MariaDbBootstrapProbe())
    {
    }

    internal MariaDbBootstrapDatabaseProvider(IMariaDbBootstrapProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        this.probe = probe;
    }

    /// <summary>
    /// Gets the logically independent MariaDB provider descriptor.
    /// </summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
        WellKnownDatabaseProviderIds.MariaDb,
        "MariaDB",
        BootstrapDatabaseTargetKind.ServerDatabase,
        BootstrapServerVersionRequirement.Required);

    /// <summary>
    /// Validates the declared MariaDB version, connection settings, server product, and target connectivity.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                database.Provider,
                WellKnownDatabaseProviderIds.MariaDb,
                StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapValidationResult.Failure("database.provider_mismatch");
        }

        if (!TryNormalizeServerVersion(database.ServerVersion, out var majorVersion, out var minorVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_invalid");
        }

        if (majorVersion < MinimumSupportedServerMajorVersion ||
            (majorVersion == MinimumSupportedServerMajorVersion &&
             minorVersion < MinimumSupportedServerMinorVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_unsupported");
        }

        if (!MariaDbDatabaseTarget.TryBuildConnectionString(database.ConnectionString, out var builder))
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }

        if (!MariaDbDatabaseTarget.TryGetValidDatabaseName(builder, out _))
        {
            return BootstrapValidationResult.Failure("database.database_required");
        }

        MariaDbDatabaseTarget.ApplySafeTimeouts(builder);

        try
        {
            var outcome = await probe.ProbeAsync(
                    builder,
                    (int)MariaDbDatabaseTarget.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                MariaDbProbeOutcome.Success => BootstrapValidationResult.Success(),
                MariaDbProbeOutcome.TargetIdentityMismatch =>
                    BootstrapValidationResult.Failure("database.connection_string_invalid"),
                MariaDbProbeOutcome.DatabaseNotFound =>
                    BootstrapValidationResult.Failure("database.target_not_found"),
                MariaDbProbeOutcome.AuthenticationFailed =>
                    BootstrapValidationResult.Failure("database.authentication_failed"),
                MariaDbProbeOutcome.TargetAccessDenied =>
                    BootstrapValidationResult.Failure("database.permission_denied"),
                MariaDbProbeOutcome.ConnectionFailed =>
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

    private static bool TryNormalizeServerVersion(
        string? serverVersion,
        out int majorVersion,
        out int minorVersion)
    {
        majorVersion = 0;
        minorVersion = 0;
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

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out majorVersion))
        {
            return false;
        }

        return parts.Length == 1 ||
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minorVersion);
    }

    private static OperationCanceledException CreateSafeCancellationException(
        CancellationToken cancellationToken) =>
        new("MariaDB bootstrap validation was cancelled by the caller.", cancellationToken);
}

internal interface IMariaDbBootstrapProbe
{
    ValueTask<MariaDbProbeOutcome> ProbeAsync(
        MySqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal enum MariaDbProbeOutcome
{
    Success,
    ServerProductMismatch,
    TargetIdentityMismatch,
    DatabaseNotFound,
    AuthenticationFailed,
    TargetAccessDenied,
    ConnectionFailed,
    ValidationFailed
}

internal sealed class MariaDbBootstrapProbe : IMariaDbBootstrapProbe
{
    public async ValueTask<MariaDbProbeOutcome> ProbeAsync(
        MySqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeTargetAsync(
                    connectionString,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "MariaDB database probe was cancelled.",
                    cancellationToken);
            }

            var targetOutcome = MariaDbProbeFailureClassifier.Classify(exception);
            if (targetOutcome is not (
                MariaDbProbeOutcome.DatabaseNotFound or
                MariaDbProbeOutcome.TargetAccessDenied))
            {
                return targetOutcome;
            }

            return await ConfirmServerProductAsync(
                    connectionString,
                    commandTimeoutSeconds,
                    targetOutcome,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<MariaDbProbeOutcome> ProbeTargetAsync(
        MySqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VERSION(), DATABASE()";
        command.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MariaDbProbeOutcome.ValidationFailed;
        }

        var serverVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
        var selectedDatabase = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!MariaDbDatabaseTarget.IsMariaDbServerVersion(serverVersion))
        {
            return MariaDbProbeOutcome.ServerProductMismatch;
        }

        return string.Equals(
            selectedDatabase,
            connectionString.Database,
            StringComparison.Ordinal)
            ? MariaDbProbeOutcome.Success
            : MariaDbProbeOutcome.TargetIdentityMismatch;
    }

    private static async ValueTask<MariaDbProbeOutcome> ConfirmServerProductAsync(
        MySqlConnectionStringBuilder targetConnectionString,
        int commandTimeoutSeconds,
        MariaDbProbeOutcome targetOutcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var serverConnectionString = new MySqlConnectionStringBuilder(
                targetConnectionString.ConnectionString)
            {
                Database = string.Empty
            };
            await using var connection = new MySqlConnection(serverConnectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT VERSION()";
            command.CommandTimeout = commandTimeoutSeconds;
            var serverVersion = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?
                .ToString();
            return MariaDbDatabaseTarget.IsMariaDbServerVersion(serverVersion)
                ? targetOutcome
                : MariaDbProbeOutcome.ServerProductMismatch;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "MariaDB server identity probe was cancelled.",
                    cancellationToken);
            }

            return MariaDbProbeFailureClassifier.Classify(exception);
        }
    }
}

internal static class MariaDbProbeFailureClassifier
{
    internal static MariaDbProbeOutcome Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mySqlException)
            {
                var classified = Classify(mySqlException.ErrorCode);
                if (classified != MariaDbProbeOutcome.ValidationFailed)
                {
                    return classified;
                }
            }

            if (current is SocketException or IOException or TimeoutException)
            {
                return MariaDbProbeOutcome.ConnectionFailed;
            }
        }

        return MariaDbProbeOutcome.ValidationFailed;
    }

    internal static MariaDbProbeOutcome Classify(MySqlErrorCode errorCode) => errorCode switch
    {
        MySqlErrorCode.UnknownDatabase => MariaDbProbeOutcome.DatabaseNotFound,
        MySqlErrorCode.AccessDenied => MariaDbProbeOutcome.AuthenticationFailed,
        MySqlErrorCode.DatabaseAccessDenied => MariaDbProbeOutcome.TargetAccessDenied,
        MySqlErrorCode.UnableToConnectToHost or
        MySqlErrorCode.ConnectionCountError or
        MySqlErrorCode.TooManyUserConnections or
        MySqlErrorCode.AbortingConnection or
        MySqlErrorCode.NewAbortingConnection or
        MySqlErrorCode.NetReadInterrupted or
        MySqlErrorCode.NetWriteInterrupted or
        MySqlErrorCode.CommandTimeoutExpired or
        MySqlErrorCode.QueryTimeout or
        MySqlErrorCode.ClientInteractionTimeout => MariaDbProbeOutcome.ConnectionFailed,
        _ => MariaDbProbeOutcome.ValidationFailed
    };
}

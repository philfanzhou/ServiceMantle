using System;
using System.Globalization;
using System.Net.Sockets;
using Npgsql;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>
/// Validates PostgreSQL bootstrap candidates.
/// </summary>
public sealed class PostgreSqlBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private const int MinimumSupportedServerMajorVersion = 15;
    private const int MaximumConnectTimeoutSeconds = 8;
    private const int CommandTimeoutSeconds = 5;

    private readonly INpgsqlBootstrapProbe probe;

    /// <summary>
    /// Initializes the PostgreSQL provider with a secure real probe implementation.
    /// </summary>
    public PostgreSqlBootstrapDatabaseProvider()
        : this(new NpgsqlBootstrapProbe())
    {
    }

    /// <summary>
    /// Initializes the PostgreSQL provider with a test probe implementation.
    /// </summary>
    /// <param name="probe">The probe implementation used during validation.</param>
    internal PostgreSqlBootstrapDatabaseProvider(INpgsqlBootstrapProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        this.probe = probe;
    }

    /// <summary>
    /// Gets the PostgreSQL provider descriptor.
    /// </summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } =
        new(
            WellKnownDatabaseProviderIds.PostgreSql,
            "PostgreSQL",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Required);

    /// <summary>
    /// Validates a PostgreSQL candidate.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!string.Equals(
            database.Provider,
            WellKnownDatabaseProviderIds.PostgreSql,
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

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        }
        catch (ArgumentException)
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }
        catch (FormatException)
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }
        catch (NpgsqlException)
        {
            return BootstrapValidationResult.Failure("database.connection_string_invalid");
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            return BootstrapValidationResult.Failure("database.database_required");
        }

        builder.Timeout = ApplySafeConnectTimeout(builder.Timeout);

        try
        {
            return await ValidateRemoteDatabaseAsync(builder, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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

        var normalized = serverVersion.Trim();
        var parts = normalized.Split('.');
        if (parts.Length > 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out majorVersion))
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 ||
                !part.All(char.IsDigit))
            {
                return false;
            }
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static int ApplySafeConnectTimeout(int timeout)
    {
        if (timeout <= 0)
        {
            return MaximumConnectTimeoutSeconds;
        }

        return Math.Min(timeout, MaximumConnectTimeoutSeconds);
    }

    private async ValueTask<BootstrapValidationResult> ValidateRemoteDatabaseAsync(
        NpgsqlConnectionStringBuilder connectionString,
        CancellationToken cancellationToken)
    {
        var outcome = await probe.ProbeAsync(connectionString, CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            PostgreSqlProbeOutcome.Success => BootstrapValidationResult.Success(),
            PostgreSqlProbeOutcome.TargetIdentityMismatch =>
                BootstrapValidationResult.Failure("database.connection_string_invalid"),
            PostgreSqlProbeOutcome.DatabaseNotFound => BootstrapValidationResult.Failure("database.target_not_found"),
            PostgreSqlProbeOutcome.AuthenticationFailed => BootstrapValidationResult.Failure("database.authentication_failed"),
            PostgreSqlProbeOutcome.TargetAccessDenied => BootstrapValidationResult.Failure("database.permission_denied"),
            PostgreSqlProbeOutcome.ConnectionFailed => BootstrapValidationResult.Failure("database.connection_failed"),
            _ => BootstrapValidationResult.Failure("database.provider_validation_failed")
        };
    }
}

internal interface INpgsqlBootstrapProbe
{
    ValueTask<PostgreSqlProbeOutcome> ProbeAsync(
        NpgsqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal enum PostgreSqlProbeOutcome
{
    Success,
    TargetIdentityMismatch,
    DatabaseNotFound,
    AuthenticationFailed,
    TargetAccessDenied,
    ConnectionFailed,
    ValidationFailed
}

internal sealed class NpgsqlBootstrapProbe : INpgsqlBootstrapProbe
{
    public async ValueTask<PostgreSqlProbeOutcome> ProbeAsync(
        NpgsqlConnectionStringBuilder connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                "SELECT current_database()::text, session_user::text",
                connection)
            {
                CommandTimeout = commandTimeoutSeconds
            };

            // PostgreSQL silently truncates startup-packet database and role identifiers to its
            // NAMEDATALEN limit. Read the identities selected by the server and compare them on the
            // client so an overlong requested name cannot be mistaken for a same-prefix target.
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PostgreSqlProbeOutcome.ValidationFailed;
            }

            var actualDatabase = reader.GetString(0);
            var actualUsername = reader.GetString(1);
            var expectedUsername = connectionString.Username;

            if (!string.Equals(actualDatabase, connectionString.Database, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(expectedUsername) &&
                 !string.Equals(actualUsername, expectedUsername, StringComparison.Ordinal)))
            {
                return PostgreSqlProbeOutcome.TargetIdentityMismatch;
            }

            return PostgreSqlProbeOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PostgreSqlProbeFailureClassifier.Classify(exception);
        }
    }
}

internal static class PostgreSqlProbeFailureClassifier
{
    public static PostgreSqlProbeOutcome Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var current = exception;
        while (current is not null)
        {
            if (current is PostgresException postgresException)
            {
                return ClassifyPostgresException(postgresException);
            }

            if (current is TimeoutException)
            {
                return PostgreSqlProbeOutcome.ConnectionFailed;
            }

            if (current is SocketException)
            {
                return PostgreSqlProbeOutcome.ConnectionFailed;
            }

            if (current is NpgsqlException)
            {
                return PostgreSqlProbeOutcome.ConnectionFailed;
            }

            current = current.InnerException;
        }

        return PostgreSqlProbeOutcome.ValidationFailed;
    }

    private static PostgreSqlProbeOutcome ClassifyPostgresException(PostgresException exception)
    {
        if (string.IsNullOrWhiteSpace(exception.SqlState))
        {
            return PostgreSqlProbeOutcome.ValidationFailed;
        }

        if (string.Equals(exception.SqlState, "3D000", StringComparison.Ordinal))
        {
            return PostgreSqlProbeOutcome.DatabaseNotFound;
        }

        if (exception.SqlState.StartsWith("28", StringComparison.Ordinal))
        {
            return PostgreSqlProbeOutcome.AuthenticationFailed;
        }

        if (string.Equals(exception.SqlState, PostgresErrorCodes.InsufficientPrivilege, StringComparison.Ordinal))
        {
            return PostgreSqlProbeOutcome.TargetAccessDenied;
        }

        if (exception.SqlState.StartsWith("08", StringComparison.Ordinal))
        {
            return PostgreSqlProbeOutcome.ConnectionFailed;
        }

        return PostgreSqlProbeOutcome.ValidationFailed;
    }
}

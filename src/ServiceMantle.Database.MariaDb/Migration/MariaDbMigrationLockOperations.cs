using System.Globalization;
using MySqlConnector;

namespace ServiceMantle.Database.MariaDb.Migration;

internal sealed class MariaDbMigrationLockOperationException()
    : Exception("The MariaDB migration lock operation failed with a safe classified outcome.");

internal interface IMariaDbMigrationLockOperations
{
    ValueTask<IMariaDbMigrationLockSession> OpenSessionAsync(
        MySqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal interface IMariaDbMigrationLockSession : IAsyncDisposable
{
    long ConnectionId { get; }

    ValueTask<int?> AcquireLockAsync(
        string lockName,
        double timeoutSeconds,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);

    ValueTask ProbeLeaseAsync(CancellationToken cancellationToken);

    ValueTask<int?> ReleaseLockAsync(string lockName, CancellationToken cancellationToken);
}

internal sealed class MariaDbMigrationLockOperations : IMariaDbMigrationLockOperations
{
    public async ValueTask<IMariaDbMigrationLockSession> OpenSessionAsync(
        MySqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        MySqlConnection? connection = null;
        try
        {
            connection = new MySqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var identity = await MariaDbMigrationLockSession.ReadIdentityAsync(
                    connection,
                    expectedDatabaseName,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!identity.IsValid)
            {
                throw new MariaDbMigrationLockOperationException();
            }

            var session = new MariaDbMigrationLockSession(
                connection,
                expectedDatabaseName,
                identity.ConnectionId);
            connection = null;
            return session;
        }
        catch (MariaDbMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new MariaDbMigrationLockOperationException();
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup cannot expose or replace the safe classified failure.
                }
            }
        }
    }
}

internal sealed class MariaDbMigrationLockSession(
    MySqlConnection connection,
    string expectedDatabaseName,
    long connectionId) : IMariaDbMigrationLockSession
{
    public long ConnectionId { get; } = connectionId;

    public async ValueTask<int?> AcquireLockAsync(
        string lockName,
        double timeoutSeconds,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@lockName, @timeoutSeconds)";
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@lockName", lockName);
        command.Parameters.AddWithValue("@timeoutSeconds", timeoutSeconds);
        return ConvertNullableInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask ProbeLeaseAsync(CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(
                connection,
                expectedDatabaseName,
                commandTimeoutSeconds: 1,
                cancellationToken)
            .ConfigureAwait(false);
        if (!identity.IsValid || identity.ConnectionId != ConnectionId)
        {
            throw new MariaDbMigrationLockOperationException();
        }
    }

    public async ValueTask<int?> ReleaseLockAsync(
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@lockName)";
        command.CommandTimeout = 1;
        command.Parameters.AddWithValue("@lockName", lockName);
        return ConvertNullableInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Closing the dedicated unpooled session is best effort during cleanup.
        }
    }

    internal static async ValueTask<MariaDbSessionIdentity> ReadIdentityAsync(
        MySqlConnection connection,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT VERSION(), @@lower_case_table_names, " +
            "BINARY DATABASE() = BINARY @databaseName, " +
            "LOWER(DATABASE()) = LOWER(@databaseName), CONNECTION_ID()";
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@databaseName", expectedDatabaseName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var serverVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
        var lowerCaseTableNames = reader.GetInt32(1);
        var exactMatch = !reader.IsDBNull(2) && reader.GetBoolean(2);
        var caseFoldedMatch = !reader.IsDBNull(3) && reader.GetBoolean(3);
        var currentConnectionId = reader.GetInt64(4);
        return new(
            MariaDbDatabaseTarget.IsMariaDbServerVersion(serverVersion) &&
            MariaDbDatabaseTarget.MatchesDatabaseIdentifierRules(
                exactMatch,
                caseFoldedMatch,
                lowerCaseTableNames),
            currentConnectionId);
    }

    private static int? ConvertNullableInt32(object? value) =>
        value is null or DBNull
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
}

internal readonly record struct MariaDbSessionIdentity(bool IsValid, long ConnectionId);

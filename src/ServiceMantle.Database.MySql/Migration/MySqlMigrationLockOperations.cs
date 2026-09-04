using System.Data.Common;
using System.Globalization;
using MySqlConnector;

namespace ServiceMantle.Database.MySql.Migration;

internal sealed class MySqlMigrationLockOperationException()
    : Exception("The MySQL migration lock operation failed with a safe classified outcome.");

internal interface IMySqlMigrationLockOperations
{
    ValueTask<IMySqlMigrationLockSession> OpenSessionAsync(
        MySqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal interface IMySqlMigrationLockSession : IAsyncDisposable
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

internal sealed class MySqlMigrationLockOperations : IMySqlMigrationLockOperations
{
    private readonly Func<MySqlConnectionStringBuilder, DbConnection> createConnection;

    internal MySqlMigrationLockOperations(
        Func<MySqlConnectionStringBuilder, DbConnection>? createConnection = null)
    {
        this.createConnection = createConnection ?? MySqlProbeConnection.Create;
    }

    public async ValueTask<IMySqlMigrationLockSession> OpenSessionAsync(
        MySqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        DbConnection? connection = null;
        try
        {
            connection = createConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var identity = await MySqlMigrationLockSession.ReadIdentityAsync(
                    connection,
                    expectedDatabaseName,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!identity.IsValid)
            {
                throw new MySqlMigrationLockOperationException();
            }

            var session = new MySqlMigrationLockSession(
                connection,
                expectedDatabaseName,
                identity.ConnectionId);
            connection = null;
            return session;
        }
        catch (MySqlMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new MySqlMigrationLockOperationException();
        }
        finally
        {
            if (connection is not null)
            {
                await MySqlProbeConnection.DisposeSafelyAsync(connection).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class MySqlMigrationLockSession(
    DbConnection connection,
    string expectedDatabaseName,
    long connectionId) : IMySqlMigrationLockSession
{
    internal const string IdentityQuery =
        "SELECT VERSION(), @@version, @@version_comment, @@lower_case_table_names, " +
        "BINARY DATABASE() = BINARY @databaseName, " +
        "LOWER(DATABASE()) = LOWER(@databaseName), CONNECTION_ID()";

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
        MySqlProbeConnection.AddParameter(command, "@lockName", lockName);
        var timeoutParameter = command.CreateParameter();
        timeoutParameter.ParameterName = "@timeoutSeconds";
        timeoutParameter.Value = timeoutSeconds;
        command.Parameters.Add(timeoutParameter);
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
            throw new MySqlMigrationLockOperationException();
        }
    }

    public async ValueTask<int?> ReleaseLockAsync(
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@lockName)";
        command.CommandTimeout = 1;
        MySqlProbeConnection.AddParameter(command, "@lockName", lockName);
        return ConvertNullableInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync() =>
        await MySqlProbeConnection.DisposeSafelyAsync(connection).ConfigureAwait(false);

    internal static async ValueTask<MySqlSessionIdentity> ReadIdentityAsync(
        DbConnection connection,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = IdentityQuery;
        command.CommandTimeout = commandTimeoutSeconds;
        MySqlProbeConnection.AddParameter(command, "@databaseName", expectedDatabaseName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (reader.FieldCount != 7 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var version = reader.GetValue(0) as string;
        var systemVersion = reader.GetValue(1) as string;
        var comment = reader.GetValue(2) as string;
        var lowerCaseTableNames = reader.GetInt32(3);
        var exactMatch = !reader.IsDBNull(4) && reader.GetBoolean(4);
        var caseFoldedMatch = !reader.IsDBNull(5) && reader.GetBoolean(5);
        var currentConnectionId = reader.GetInt64(6);
        var extraRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(
            !extraRow && MySqlProductIdentity.IsSupported(
                connection.ServerVersion,
                version,
                systemVersion,
                comment) &&
            MySqlDatabaseTarget.MatchesDatabaseIdentifierRules(
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

internal readonly record struct MySqlSessionIdentity(bool IsValid, long ConnectionId);

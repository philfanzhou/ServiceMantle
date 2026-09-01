using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace ServiceMantle.Database.SqlServer.Migration;

internal enum SqlServerMigrationLockFailureKind
{
    NotSupported,
    Failed
}

internal sealed class SqlServerMigrationLockOperationException(
    SqlServerMigrationLockFailureKind kind)
    : Exception("The SQL Server migration lock operation failed with a safe classified outcome.")
{
    internal SqlServerMigrationLockFailureKind Kind { get; } = kind;
}

internal interface ISqlServerMigrationLockOperations
{
    ValueTask<ISqlServerMigrationLockSession> OpenSessionAsync(
        SqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal interface ISqlServerMigrationLockSession : IAsyncDisposable
{
    int SessionId { get; }

    ValueTask<int> AcquireLockAsync(
        string resourceName,
        int timeoutMilliseconds,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);

    ValueTask ProbeLeaseAsync(string resourceName, CancellationToken cancellationToken);

    ValueTask<int> ReleaseLockAsync(
        string resourceName,
        CancellationToken cancellationToken);
}

internal sealed class SqlServerMigrationLockOperations : ISqlServerMigrationLockOperations
{
    public async ValueTask<ISqlServerMigrationLockSession> OpenSessionAsync(
        SqlConnectionStringBuilder connectionString,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(
                    connection,
                    expectedDatabaseName,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!identity.IsValid)
            {
                throw new SqlServerMigrationLockOperationException(
                    SqlServerMigrationLockFailureKind.Failed);
            }

            var session = new SqlServerMigrationLockSession(connection, identity.SessionId);
            connection = null;
            return session;
        }
        catch (SqlServerMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqlServerMigrationLockOperationException(
                SqlServerMigrationLockFailureKind.Failed);
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
                    // Cleanup cannot expose or replace the classified failure.
                }
            }
        }
    }

    private static async ValueTask<SqlServerSessionIdentity> ReadIdentityAsync(
        SqlConnection connection,
        string expectedDatabaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), DB_NAME(), @@SPID";
        command.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0) ||
            reader.IsDBNull(1) ||
            reader.IsDBNull(2))
        {
            return default;
        }

        return new(
            reader.GetInt32(0) >= SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion &&
            string.Equals(reader.GetString(1), expectedDatabaseName, StringComparison.Ordinal),
            reader.GetInt16(2));
    }
}

internal sealed class SqlServerMigrationLockSession(
    SqlConnection connection,
    int sessionId) : ISqlServerMigrationLockSession
{
    public int SessionId { get; } = sessionId;

    public ValueTask<int> AcquireLockAsync(
        string resourceName,
        int timeoutMilliseconds,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        ExecuteApplicationLockAsync(
            "sys.sp_getapplock",
            resourceName,
            timeoutMilliseconds,
            commandTimeoutSeconds,
            cancellationToken,
            classifyPermissionDenied: true);

    public async ValueTask ProbeLeaseAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT APPLOCK_MODE(N'public', @resourceName, N'Session'), @@SPID";
            command.CommandTimeout = 1;
            command.Parameters.Add("@resourceName", SqlDbType.NVarChar, 255).Value = resourceName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.IsDBNull(0) ||
                reader.IsDBNull(1) ||
                !string.Equals(reader.GetString(0), "Exclusive", StringComparison.Ordinal) ||
                reader.GetInt16(1) != SessionId)
            {
                throw new SqlServerMigrationLockOperationException(
                    SqlServerMigrationLockFailureKind.Failed);
            }
        }
        catch (SqlServerMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqlServerMigrationLockOperationException(
                SqlServerMigrationLockFailureKind.Failed);
        }
    }

    public ValueTask<int> ReleaseLockAsync(
        string resourceName,
        CancellationToken cancellationToken) =>
        ExecuteApplicationLockAsync(
            "sys.sp_releaseapplock",
            resourceName,
            timeoutMilliseconds: null,
            commandTimeoutSeconds: 1,
            cancellationToken,
            classifyPermissionDenied: false);

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

    internal static bool IsApplicationLockPermissionDenied(int errorNumber) =>
        errorNumber is 229 or 15151;

    private async ValueTask<int> ExecuteApplicationLockAsync(
        string procedureName,
        string resourceName,
        int? timeoutMilliseconds,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        bool classifyPermissionDenied)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = procedureName;
            command.CommandTimeout = commandTimeoutSeconds;
            var returnValue = command.Parameters.Add("@returnValue", SqlDbType.Int);
            returnValue.Direction = ParameterDirection.ReturnValue;
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resourceName;
            command.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Session";
            command.Parameters.Add("@DbPrincipal", SqlDbType.NVarChar, 128).Value = "public";
            if (timeoutMilliseconds is not null)
            {
                command.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value = "Exclusive";
                command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = timeoutMilliseconds.Value;
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(returnValue.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqlServerMigrationLockOperationException(
                classifyPermissionDenied && ContainsPermissionDenied(exception)
                    ? SqlServerMigrationLockFailureKind.NotSupported
                    : SqlServerMigrationLockFailureKind.Failed);
        }
    }

    private static bool ContainsPermissionDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException &&
                sqlException.Errors.Cast<SqlError>().Any(error =>
                    IsApplicationLockPermissionDenied(error.Number)))
            {
                return true;
            }
        }

        return false;
    }
}

internal readonly record struct SqlServerSessionIdentity(bool IsValid, int SessionId);

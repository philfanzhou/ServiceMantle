using System.Data;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;

namespace ServiceMantle.Database.Oracle.Migration;

internal enum OracleMigrationLockFailureKind
{
    NotSupported,
    Failed
}

internal sealed class OracleMigrationLockOperationException(OracleMigrationLockFailureKind kind)
    : Exception("The Oracle migration lock operation failed with a classified safe outcome.")
{
    internal OracleMigrationLockFailureKind Kind { get; } = kind;
}

internal interface IOracleMigrationLockOperations
{
    ValueTask<IOracleMigrationLockSession> OpenSessionAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken);
}

internal interface IOracleMigrationLockSession : IAsyncDisposable
{
    long SessionId { get; }

    ValueTask<string> AllocateLockHandleAsync(
        string lockName,
        CancellationToken cancellationToken);

    ValueTask<int> RequestLockAsync(
        string lockHandle,
        int timeoutSeconds,
        CancellationToken cancellationToken);

    ValueTask ProbeLeaseAsync(CancellationToken cancellationToken);

    ValueTask<int> ReleaseLockAsync(
        string lockHandle,
        CancellationToken cancellationToken);
}

internal sealed class OracleMigrationLockOperations : IOracleMigrationLockOperations
{
    public async ValueTask<IOracleMigrationLockSession> OpenSessionAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        OracleConnection? connection = null;
        try
        {
            connection = new OracleConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var topology = await OracleRuntimeTopology.ProbeAsync(
                    connection,
                    expectedUserName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (topology != OracleTargetProbeOutcome.Success)
            {
                throw new OracleMigrationLockOperationException(
                    OracleMigrationLockFailureKind.Failed);
            }

            var sessionId = await ReadSessionIdAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            var session = new OracleMigrationLockSession(connection, sessionId);
            connection = null;
            return session;
        }
        catch (OracleMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OracleMigrationLockOperationException(
                OracleMigrationLockFailureKind.Failed);
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

    private static async ValueTask<long> ReadSessionIdAsync(
        OracleConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
        command.CommandText = "SELECT SYS_CONTEXT('USERENV', 'SID') FROM DUAL";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

internal sealed class OracleMigrationLockSession(OracleConnection connection, long sessionId)
    : IOracleMigrationLockSession
{
    public long SessionId { get; } = sessionId;

    public async ValueTask<string> AllocateLockHandleAsync(
        string lockName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
            command.CommandText =
                "BEGIN SYS.DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS(" +
                "lockname => :lock_name, lockhandle => :lock_handle); END;";
            command.Parameters.Add(
                "lock_name",
                OracleDbType.Varchar2,
                lockName,
                ParameterDirection.Input);
            var handle = command.Parameters.Add(
                "lock_handle",
                OracleDbType.Varchar2,
                128,
                null,
                ParameterDirection.Output);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var value = handle.Value?.ToString();
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new OracleMigrationLockOperationException(
                    OracleMigrationLockFailureKind.Failed);
        }
        catch (OracleMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ThrowClassified(exception, cancellationToken, classifyMissingPrivilege: true);
            throw;
        }
    }

    public ValueTask<int> RequestLockAsync(
        string lockHandle,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        ExecuteLockFunctionAsync(
            "BEGIN :result_code := SYS.DBMS_LOCK.REQUEST(" +
            "lockhandle => :lock_handle, lockmode => SYS.DBMS_LOCK.X_MODE, " +
            "timeout => :timeout_seconds, release_on_commit => FALSE); END;",
            lockHandle,
            timeoutSeconds,
            cancellationToken,
            classifyMissingPrivilege: true);

    public async ValueTask ProbeLeaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "SELECT 1 FROM DUAL";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ThrowClassified(exception, cancellationToken, classifyMissingPrivilege: false);
        }
    }

    public ValueTask<int> ReleaseLockAsync(
        string lockHandle,
        CancellationToken cancellationToken) =>
        ExecuteLockFunctionAsync(
            "BEGIN :result_code := SYS.DBMS_LOCK.RELEASE(lockhandle => :lock_handle); END;",
            lockHandle,
            timeoutSeconds: null,
            cancellationToken,
            classifyMissingPrivilege: false);

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

    private async ValueTask<int> ExecuteLockFunctionAsync(
        string commandText,
        string lockHandle,
        int? timeoutSeconds,
        CancellationToken cancellationToken,
        bool classifyMissingPrivilege)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandTimeout = timeoutSeconds is null
                ? OracleDatabaseTarget.CommandTimeoutSeconds
                : Math.Max(
                    1,
                    timeoutSeconds.Value == int.MaxValue
                        ? int.MaxValue
                        : timeoutSeconds.Value + 1);
            command.CommandText = commandText;
            var result = command.Parameters.Add(
                "result_code",
                OracleDbType.Int32,
                ParameterDirection.Output);
            command.Parameters.Add(
                "lock_handle",
                OracleDbType.Varchar2,
                lockHandle,
                ParameterDirection.Input);
            if (timeoutSeconds is not null)
            {
                command.Parameters.Add(
                    "timeout_seconds",
                    OracleDbType.Int32,
                    timeoutSeconds.Value,
                    ParameterDirection.Input);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return int.TryParse(
                    result.Value?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var resultCode)
                ? resultCode
                : throw new OracleMigrationLockOperationException(
                    OracleMigrationLockFailureKind.Failed);
        }
        catch (OracleMigrationLockOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ThrowClassified(exception, cancellationToken, classifyMissingPrivilege);
            throw;
        }
    }

    private static void ThrowClassified(
        Exception exception,
        CancellationToken cancellationToken,
        bool classifyMissingPrivilege)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OracleMigrationLockOperationException(
            classifyMissingPrivilege && OracleFailureClassifier.IsTopologyPermissionDenied(exception)
                ? OracleMigrationLockFailureKind.NotSupported
                : OracleMigrationLockFailureKind.Failed);
    }
}

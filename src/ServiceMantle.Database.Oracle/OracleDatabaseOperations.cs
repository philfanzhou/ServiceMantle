using System.Data;
using System.Globalization;
using System.Net.Sockets;
using Oracle.ManagedDataAccess.Client;

namespace ServiceMantle.Database.Oracle;

internal enum OracleTargetProbeOutcome
{
    Success,
    IdentityMismatch,
    UnsupportedTopology,
    TopologyPermissionDenied,
    CreateSessionDenied,
    AccountLocked,
    PasswordExpired,
    InvalidCredentials,
    ConnectionFailed,
    ValidationFailed
}

internal enum OracleUserMatch
{
    Missing,
    Exact,
    Conflicting
}

internal enum OracleFailureKind
{
    AuthenticationFailed,
    PermissionDenied,
    TargetConflict,
    ConnectionFailed,
    InvalidTarget,
    Unexpected
}

internal sealed class OracleOperationException(OracleFailureKind kind) : Exception(
    "The Oracle database operation failed with a classified safe outcome.")
{
    internal OracleFailureKind Kind { get; } = kind;
}

internal interface IOracleDatabaseOperations
{
    ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken);

    ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken);
}

internal interface IOracleAdministrativeSession : IAsyncDisposable
{
    ValueTask<OracleUserMatch> FindUserAsync(string userName, CancellationToken cancellationToken);

    ValueTask CreateUserAsync(
        string userName,
        string password,
        CancellationToken cancellationToken);

    ValueTask GrantCreateSessionAsync(string userName, CancellationToken cancellationToken);

    ValueTask DropUserAsync(string userName, CancellationToken cancellationToken);
}

internal sealed class OracleDatabaseOperations : IOracleDatabaseOperations
{
    public async ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        OracleConnection? connection = null;
        try
        {
            connection = new OracleConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await OracleRuntimeTopology.ProbeAsync(
                    connection,
                    expectedUserName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Oracle target observation was cancelled.",
                    cancellationToken);
            }

            return OracleFailureClassifier.ClassifyTargetProbe(exception);
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
                    // Cleanup cannot replace the safe observation result.
                }
            }
        }
    }

    public async ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
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
                throw new OracleOperationException(topology switch
                {
                    OracleTargetProbeOutcome.TopologyPermissionDenied => OracleFailureKind.PermissionDenied,
                    OracleTargetProbeOutcome.IdentityMismatch or
                    OracleTargetProbeOutcome.UnsupportedTopology => OracleFailureKind.InvalidTarget,
                    OracleTargetProbeOutcome.ConnectionFailed => OracleFailureKind.ConnectionFailed,
                    _ => OracleFailureKind.Unexpected
                });
            }

            var session = new OracleAdministrativeSession(connection);
            connection = null;
            return session;
        }
        catch (OracleOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Oracle administrative connection was cancelled.",
                    cancellationToken);
            }

            throw new OracleOperationException(OracleFailureClassifier.ClassifyPreparation(exception));
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
}

internal sealed class OracleAdministrativeSession(OracleConnection connection)
    : IOracleAdministrativeSession
{
    public async ValueTask<OracleUserMatch> FindUserAsync(
        string userName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
            command.CommandText =
                "SELECT COMMON, ORACLE_MAINTAINED FROM ALL_USERS WHERE USERNAME = :user_name";
            command.Parameters.Add("user_name", OracleDbType.Varchar2, userName, ParameterDirection.Input);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return OracleUserMatch.Missing;
            }

            var common = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var oracleMaintained = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            return string.Equals(common, "NO", StringComparison.Ordinal) &&
                string.Equals(oracleMaintained, "N", StringComparison.Ordinal)
                ? OracleUserMatch.Exact
                : OracleUserMatch.Conflicting;
        }
        catch (Exception exception)
        {
            ThrowClassified(exception, cancellationToken);
            throw;
        }
    }

    public ValueTask CreateUserAsync(
        string userName,
        string password,
        CancellationToken cancellationToken) =>
        ExecuteDdlAsync(
            $"CREATE USER {OracleDatabaseTarget.QuoteIdentifier(userName)} " +
            $"IDENTIFIED BY {OracleDatabaseTarget.QuotePassword(password)}",
            cancellationToken);

    public ValueTask GrantCreateSessionAsync(
        string userName,
        CancellationToken cancellationToken) =>
        ExecuteDdlAsync(
            $"GRANT CREATE SESSION TO {OracleDatabaseTarget.QuoteIdentifier(userName)}",
            cancellationToken);

    public ValueTask DropUserAsync(string userName, CancellationToken cancellationToken) =>
        ExecuteDdlAsync(
            $"DROP USER {OracleDatabaseTarget.QuoteIdentifier(userName)}",
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is deliberately best effort and never exposes connection details.
        }
    }

    private async ValueTask ExecuteDdlAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ThrowClassified(exception, cancellationToken);
        }
    }

    private static void ThrowClassified(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Oracle administrative operation was cancelled.",
                cancellationToken);
        }

        throw new OracleOperationException(OracleFailureClassifier.ClassifyPreparation(exception));
    }
}

internal static class OracleRuntimeTopology
{
    internal static async ValueTask<OracleTargetProbeOutcome> ProbeAsync(
        OracleConnection connection,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
            command.CommandText =
                "SELECT SYS_CONTEXT('USERENV', 'SESSION_USER'), " +
                "SYS_CONTEXT('USERENV', 'CDB_NAME'), SYS_CONTEXT('USERENV', 'CON_ID'), " +
                "SYS_CONTEXT('USERENV', 'IS_APPLICATION_ROOT'), " +
                "SYS_CONTEXT('USERENV', 'IS_APPLICATION_PDB'), " +
                "SYS_CONTEXT('USERENV', 'CLOUD_SERVICE') FROM DUAL";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return OracleTargetProbeOutcome.ValidationFailed;
            }

            var sessionUser = ReadString(reader, 0);
            var cdbName = ReadString(reader, 1);
            var conIdValue = ReadString(reader, 2);
            var applicationRoot = ReadString(reader, 3);
            var applicationPdb = ReadString(reader, 4);
            var cloudService = ReadString(reader, 5);
            if (!string.Equals(sessionUser, expectedUserName, StringComparison.Ordinal))
            {
                return OracleTargetProbeOutcome.IdentityMismatch;
            }

            if (cdbName.Length == 0 ||
                !int.TryParse(conIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var conId) ||
                conId <= 2 ||
                !string.Equals(applicationRoot, "NO", StringComparison.Ordinal) ||
                !string.Equals(applicationPdb, "NO", StringComparison.Ordinal) ||
                cloudService.Length != 0)
            {
                return OracleTargetProbeOutcome.UnsupportedTopology;
            }

            await using var clusterCommand = connection.CreateCommand();
            clusterCommand.BindByName = true;
            clusterCommand.CommandTimeout = OracleDatabaseTarget.CommandTimeoutSeconds;
            clusterCommand.CommandText =
                "BEGIN IF DBMS_UTILITY.IS_CLUSTER_DATABASE THEN :is_cluster := 'TRUE'; " +
                "ELSE :is_cluster := 'FALSE'; END IF; END;";
            var clusterParameter = clusterCommand.Parameters.Add(
                "is_cluster",
                OracleDbType.Varchar2,
                5,
                null,
                ParameterDirection.Output);
            await clusterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(clusterParameter.Value?.ToString(), "FALSE", StringComparison.Ordinal)
                ? OracleTargetProbeOutcome.Success
                : OracleTargetProbeOutcome.UnsupportedTopology;
        }
        catch (OracleException exception) when (exception.Number == 1031)
        {
            return OracleTargetProbeOutcome.TopologyPermissionDenied;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Oracle topology probing was cancelled.", cancellationToken);
            }

            return OracleFailureClassifier.ClassifyTargetProbe(exception) switch
            {
                OracleTargetProbeOutcome.ConnectionFailed => OracleTargetProbeOutcome.ConnectionFailed,
                _ => OracleTargetProbeOutcome.ValidationFailed
            };
        }
    }

    private static string ReadString(OracleDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal).Trim();
}

internal static class OracleFailureClassifier
{
    internal static OracleTargetProbeOutcome ClassifyTargetProbe(Exception exception)
    {
        var number = FindOracleErrorNumber(exception);
        return number switch
        {
            1045 => OracleTargetProbeOutcome.CreateSessionDenied,
            28000 => OracleTargetProbeOutcome.AccountLocked,
            28001 => OracleTargetProbeOutcome.PasswordExpired,
            1017 => OracleTargetProbeOutcome.InvalidCredentials,
            not null when IsConnectionFailure(number.Value) => OracleTargetProbeOutcome.ConnectionFailed,
            _ when ContainsTransportFailure(exception) => OracleTargetProbeOutcome.ConnectionFailed,
            _ => OracleTargetProbeOutcome.ValidationFailed
        };
    }

    internal static OracleFailureKind ClassifyPreparation(Exception exception)
    {
        var number = FindOracleErrorNumber(exception);
        return number switch
        {
            1017 or 28000 or 28001 => OracleFailureKind.AuthenticationFailed,
            1031 or 1045 => OracleFailureKind.PermissionDenied,
            1918 or 1920 => OracleFailureKind.TargetConflict,
            not null when IsConnectionFailure(number.Value) => OracleFailureKind.ConnectionFailed,
            _ when ContainsTransportFailure(exception) => OracleFailureKind.ConnectionFailed,
            _ => OracleFailureKind.Unexpected
        };
    }

    internal static int? FindOracleErrorNumber(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OracleException oracleException)
            {
                return oracleException.Number;
            }
        }

        return null;
    }

    internal static bool IsConnectionFailure(int number) => number is
        3113 or 3114 or 3135 or 12154 or 12170 or 12514 or 12516 or 12518 or 12520 or
        12528 or 12535 or 12537 or 12541 or 12543 or 12545 or 12547 or 12560 or 12571;

    private static bool ContainsTransportFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException or IOException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}

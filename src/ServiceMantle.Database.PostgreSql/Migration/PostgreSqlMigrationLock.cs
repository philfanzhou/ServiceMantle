using Npgsql;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>
/// A session-level PostgreSQL advisory lock lease.
/// The lock is held by a dedicated, open connection and is released when the connection is closed.
/// </summary>
internal sealed class PostgreSqlMigrationLock : IDatabaseMigrationLock
{
    private readonly NpgsqlConnection connection;
    private readonly long lockKey;
    private bool unlockAttempted;
    private bool disposed;

    /// <summary>
    /// Initializes a PostgreSQL migration lock.
    /// </summary>
    /// <param name="connection">An open connection holding the lock.</param>
    /// <param name="lockKey">The advisory lock key.</param>
    public PostgreSqlMigrationLock(NpgsqlConnection connection, long lockKey)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.lockKey = lockKey;
    }

    public string ProviderId => "PostgreSQL";

    /// <summary>
    /// Attempts to unlock and dispose the connection. Errors are suppressed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            // Attempt explicit unlock while the connection is still open.
            if (!unlockAttempted && connection.State == System.Data.ConnectionState.Open)
            {
                unlockAttempted = true;
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@lock_key)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@lock_key";
                parameter.Value = lockKey;
                command.Parameters.Add(parameter);

                try
                {
                    await command.ExecuteScalarAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Explicit unlock failed, but the connection will still release the lock on close.
                }
            }
        }
        finally
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Connection disposal errors do not mask the lock release flow.
            }
        }
    }
}

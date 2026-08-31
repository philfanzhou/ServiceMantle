using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>
/// Provides session-level PostgreSQL advisory lock capability for multi-instance migration safety.
/// Uses pg_try_advisory_lock with bounded polling under a single overall timeout that covers
/// connection, command execution, and waiting. An acquired lease probes its dedicated connection
/// every 250 milliseconds with a one-second command timeout and signals detected lease loss within
/// a conservative five-second running-process bound.
/// </summary>
public sealed class PostgreSqlMigrationLockProvider : IDatabaseMigrationLockProvider
{
    private const int PollIntervalMs = 100;

    public string ProviderId => WellKnownDatabaseProviderIds.PostgreSql;

    /// <summary>
    /// Acquires a session-level PostgreSQL advisory lock using try_lock with bounded polling.
    /// The acquireTimeout covers the entire operation: connection, SQL execution, and polling.
    /// The returned lease also monitors its dedicated session until disposal.
    /// </summary>
    public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(bootstrap);

        if (acquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lock acquire timeout must be positive.",
                nameof(acquireTimeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var lockKey = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        NpgsqlConnection? connection = null;
        try
        {
            // Create linked CTS for both caller cancellation and overall timeout
            using var timeoutCts = new CancellationTokenSource(acquireTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            var effectiveToken = linkedCts.Token;

            // Open connection under the overall timeout
            connection = new NpgsqlConnection(bootstrap.ConnectionString);

            try
            {
                await connection.OpenAsync(effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Overall timeout triggered
                throw new DatabaseMigrationLockException(
                    WellKnownMigrationErrorCodes.LockTimeout,
                    "Lock acquisition timed out while opening database connection.");
            }
            catch (Exception)
            {
                throw new DatabaseMigrationLockException(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Failed to open database connection.");
            }

            // Bounded polling: attempt to acquire the lock with try_lock
            while (true)
            {
                // Check if caller cancelled or overall timeout triggered
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                if (timeoutCts.Token.IsCancellationRequested)
                {
                    throw new DatabaseMigrationLockException(
                        WellKnownMigrationErrorCodes.LockTimeout,
                        "Lock acquisition timed out during polling.");
                }

                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT pg_try_advisory_lock(@lock_key)";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@lock_key";
                    parameter.Value = lockKey;
                    command.Parameters.Add(parameter);

                    var result = await command.ExecuteScalarAsync(effectiveToken).ConfigureAwait(false);
                    if (result is bool lockAcquired && lockAcquired)
                    {
                        // Successfully acquired the lock
                        return new PostgreSqlMigrationLock(connection, lockKey);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // Overall timeout triggered
                    throw new DatabaseMigrationLockException(
                        WellKnownMigrationErrorCodes.LockTimeout,
                        "Lock acquisition timed out during command execution.");
                }
                catch (Exception ex)
                {
                    throw new DatabaseMigrationLockException(
                        WellKnownMigrationErrorCodes.LockFailed,
                        $"Lock acquisition command failed: {ex.GetType().Name}");
                }

                // Lock is held by another instance. Wait and retry.
                try
                {
                    await Task.Delay(PollIntervalMs, effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // Overall timeout triggered
                    throw new DatabaseMigrationLockException(
                        WellKnownMigrationErrorCodes.LockTimeout,
                        "Lock acquisition timed out during polling wait.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean up connection on cancellation
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress cleanup errors
                }
            }

            throw;
        }
        catch (DatabaseMigrationLockException)
        {
            // Clean up connection on expected lock failure
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress cleanup errors
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            // Clean up connection on unexpected error
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress cleanup errors
                }
            }

            throw new DatabaseMigrationLockException(
                WellKnownMigrationErrorCodes.LockFailed,
                $"Unexpected error during lock acquisition: {ex.GetType().Name}");
        }
    }
}

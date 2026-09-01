using System.Data;
using Npgsql;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>A session-level PostgreSQL advisory lock lease held by a dedicated connection.</summary>
internal sealed class PostgreSqlMigrationLock : IDatabaseMigrationLock
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);
    private const int ProbeCommandTimeoutSeconds = 1;

    /// <summary>
    /// Conservative running-process deadline used by the provider contract and real-connection
    /// tests. It includes the probe interval, command timeout, and scheduling margin.
    /// </summary>
    internal static TimeSpan LeaseLossDetectionBound { get; } = TimeSpan.FromSeconds(5);

    private readonly NpgsqlConnection connection;
    private readonly long lockKey;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly CancellationTokenSource leaseLost = new();
    private readonly Task monitorTask;
    private int disposeStarted;
    private bool unlockAttempted;

    /// <summary>Initializes a PostgreSQL migration lock around an acquired session lease.</summary>
    public PostgreSqlMigrationLock(NpgsqlConnection connection, long lockKey)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.lockKey = lockKey;
        connection.StateChange += OnConnectionStateChange;
        monitorTask = MonitorConnectionAsync();
    }

    public string ProviderId => "PostgreSQL";

    public CancellationToken LeaseLost => leaseLost.Token;

    internal int BackendProcessId => connection.ProcessID;

    /// <summary>Stops monitoring, attempts unlock, and disposes the dedicated connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        connection.StateChange -= OnConnectionStateChange;
        await monitorCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch
        {
            // Monitoring failures are converted to LeaseLost and never replace cleanup.
        }

        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!unlockAttempted && connection.State == ConnectionState.Open)
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
                    // Closing the session remains the authoritative release fallback.
                }
            }
        }
        finally
        {
            connectionGate.Release();
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup errors do not mask the orchestration result.
            }

            monitorCancellation.Dispose();
            leaseLost.Dispose();
            connectionGate.Dispose();
        }
    }

    private async Task MonitorConnectionAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProbeInterval, monitorCancellation.Token).ConfigureAwait(false);
                await connectionGate.WaitAsync(monitorCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref disposeStarted) != 0)
                    {
                        return;
                    }

                    if (connection.State != ConnectionState.Open)
                    {
                        SignalLeaseLost();
                        return;
                    }

                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    command.CommandTimeout = ProbeCommandTimeoutSeconds;
                    await command.ExecuteScalarAsync(monitorCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    connectionGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
            // Explicit disposal stops monitoring without reporting lease loss.
        }
        catch
        {
            if (Volatile.Read(ref disposeStarted) == 0)
            {
                SignalLeaseLost();
            }
        }
    }

    private void OnConnectionStateChange(object sender, StateChangeEventArgs args)
    {
        if (Volatile.Read(ref disposeStarted) == 0 &&
            args.CurrentState is ConnectionState.Broken or ConnectionState.Closed)
        {
            SignalLeaseLost();
        }
    }

    private void SignalLeaseLost()
    {
        try
        {
            leaseLost.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Explicit disposal owns the terminal state once cleanup has begun.
        }
    }
}

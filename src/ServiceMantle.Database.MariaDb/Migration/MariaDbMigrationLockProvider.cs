using System.Diagnostics;
using System.Globalization;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.MariaDb.Migration;

/// <summary>
/// Provides an exclusive MariaDB migration lock backed by <c>GET_LOCK</c> on a dedicated,
/// unpooled target-database session.
/// </summary>
public sealed class MariaDbMigrationLockProvider : IDatabaseMigrationLockProvider
{
    private static readonly TimeSpan MaximumAcquireTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly IMariaDbMigrationLockOperations operations;

    /// <summary>Initializes the provider with real MySqlConnector database operations.</summary>
    public MariaDbMigrationLockProvider()
        : this(new MariaDbMigrationLockOperations())
    {
    }

    internal MariaDbMigrationLockProvider(IMariaDbMigrationLockOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    /// <summary>Gets the canonical MariaDB provider ID.</summary>
    public string ProviderId => WellKnownDatabaseProviderIds.MariaDb;

    /// <summary>
    /// Acquires the service lock within one timeout shared by target connection, identity
    /// validation, and the parameterized <c>GET_LOCK</c> call.
    /// </summary>
    public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (acquireTimeout <= TimeSpan.Zero || acquireTimeout > MaximumAcquireTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acquireTimeout),
                acquireTimeout,
                $"Lock acquire timeout must be positive and no greater than {MaximumAcquireTimeout}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildTarget(bootstrap, out var connectionString, out var databaseName))
        {
            throw Failure("The MariaDB migration lock target is invalid or unsupported.");
        }

        var elapsed = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(acquireTimeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var lockName = MariaDbMigrationLockName.Derive(serviceId);
        IMariaDbMigrationLockSession? session = null;
        try
        {
            var commandTimeoutSeconds = GetRemainingCommandTimeoutSeconds(
                acquireTimeout,
                elapsed.Elapsed);
            session = await operations.OpenSessionAsync(
                    connectionString,
                    databaseName,
                    commandTimeoutSeconds,
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = GetRemaining(acquireTimeout, elapsed.Elapsed);
            var result = await session.AcquireLockAsync(
                    lockName,
                    remaining.TotalSeconds,
                    GetCommandTimeoutSeconds(remaining),
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result == 0 || timeoutSource.IsCancellationRequested)
            {
                throw TimeoutFailure();
            }

            if (result != 1)
            {
                throw Failure("MariaDB did not acquire the migration lock.");
            }

            var lease = new MariaDbMigrationLock(session, lockName);
            session = null;
            return lease;
        }
        catch (DatabaseMigrationLockException)
        {
            ThrowCallerCancellation(cancellationToken);
            throw;
        }
        catch (OperationCanceledException)
        {
            ThrowCallerCancellation(cancellationToken);
            throw timeoutSource.IsCancellationRequested
                ? TimeoutFailure()
                : Failure("MariaDB migration lock acquisition failed.");
        }
        catch (Exception)
        {
            ThrowCallerCancellation(cancellationToken);
            throw timeoutSource.IsCancellationRequested
                ? TimeoutFailure()
                : Failure("MariaDB migration lock acquisition failed.");
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup cannot replace the safe classified acquisition outcome.
                }
            }
        }
    }

    private static bool TryBuildTarget(
        BootstrapDatabaseConfiguration bootstrap,
        out MySqlConnectionStringBuilder connectionString,
        out string databaseName)
    {
        connectionString = null!;
        databaseName = string.Empty;
        if (!string.Equals(
                bootstrap.Provider,
                WellKnownDatabaseProviderIds.MariaDb,
                StringComparison.OrdinalIgnoreCase) ||
            !IsSupportedServerVersion(bootstrap.ServerVersion) ||
            !MariaDbDatabaseTarget.TryBuildConnectionString(
                bootstrap.ConnectionString,
                out connectionString) ||
            !MariaDbDatabaseTarget.TryGetValidDatabaseName(connectionString, out databaseName))
        {
            return false;
        }

        connectionString.Pooling = false;
        connectionString.AutoEnlist = false;
        MariaDbDatabaseTarget.ApplySafeTimeouts(connectionString);
        return true;
    }

    private static bool IsSupportedServerVersion(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        var parts = serverVersion.Trim().Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(part =>
                part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = parts.Length == 1
            ? 0
            : int.Parse(parts[1], CultureInfo.InvariantCulture);
        return major > 10 || major == 10 && minor >= 11;
    }

    private static TimeSpan GetRemaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : throw TimeoutFailure();
    }

    private static int GetRemainingCommandTimeoutSeconds(TimeSpan timeout, TimeSpan elapsed) =>
        GetCommandTimeoutSeconds(GetRemaining(timeout, elapsed));

    private static int GetCommandTimeoutSeconds(TimeSpan remaining) =>
        (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds) + 1D, 1D, int.MaxValue);

    private static void ThrowCallerCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "MariaDB migration lock acquisition was cancelled by the caller.",
                cancellationToken);
        }
    }

    private static DatabaseMigrationLockException TimeoutFailure() => new(
        WellKnownMigrationErrorCodes.LockTimeout,
        "MariaDB migration lock acquisition timed out.");

    private static DatabaseMigrationLockException Failure(string message) => new(
        WellKnownMigrationErrorCodes.LockFailed,
        message);
}

internal sealed class MariaDbMigrationLock : IDatabaseMigrationLock
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan LeaseLossDetectionBound { get; } = TimeSpan.FromSeconds(5);

    private readonly IMariaDbMigrationLockSession session;
    private readonly string lockName;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly CancellationTokenSource leaseLost = new();
    private readonly Task monitorTask;
    private int disposeStarted;

    internal MariaDbMigrationLock(IMariaDbMigrationLockSession session, string lockName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        this.session = session;
        this.lockName = lockName;
        monitorTask = MonitorSessionAsync();
    }

    public string ProviderId => WellKnownDatabaseProviderIds.MariaDb;

    public CancellationToken LeaseLost => leaseLost.Token;

    internal long ConnectionId => session.ConnectionId;

    /// <summary>Stops monitoring, releases the named lock once, and closes the session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        await monitorCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch
        {
            // Monitoring failures are converted to LeaseLost and never replace cleanup.
        }

        await sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await session.ReleaseLockAsync(lockName, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Closing the session remains the authoritative release fallback.
            }
        }
        finally
        {
            sessionGate.Release();
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup cannot mask the orchestration result.
            }

            monitorCancellation.Dispose();
            leaseLost.Dispose();
            sessionGate.Dispose();
        }
    }

    private async Task MonitorSessionAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProbeInterval, monitorCancellation.Token).ConfigureAwait(false);
                await sessionGate.WaitAsync(monitorCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref disposeStarted) != 0)
                    {
                        return;
                    }

                    await session.ProbeLeaseAsync(monitorCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    sessionGate.Release();
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
    }
}

using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.Oracle.Migration;

/// <summary>
/// Provides an exclusive Oracle migration lock backed by <c>SYS.DBMS_LOCK</c> on a dedicated,
/// unpooled target-user session.
/// </summary>
public sealed class OracleMigrationLockProvider : IDatabaseMigrationLockProvider
{
    private static readonly TimeSpan MaximumAcquireTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly IOracleMigrationLockOperations operations;

    /// <summary>Initializes the provider with real ODP.NET database operations.</summary>
    public OracleMigrationLockProvider()
        : this(new OracleMigrationLockOperations())
    {
    }

    internal OracleMigrationLockProvider(IOracleMigrationLockOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    /// <summary>Gets the canonical Oracle provider ID.</summary>
    public string ProviderId => WellKnownDatabaseProviderIds.Oracle;

    /// <summary>
    /// Acquires the service lock within one bounded timeout covering connection, allocation, and
    /// <c>DBMS_LOCK.REQUEST</c>. The target user must have a direct
    /// <c>EXECUTE ON SYS.DBMS_LOCK</c> grant.
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
        if (!TryBuildTarget(bootstrap, out var connectionString, out var targetUserName))
        {
            throw Failure(
                WellKnownMigrationErrorCodes.LockFailed,
                "The Oracle migration lock target is invalid or unsupported.");
        }

        var elapsed = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(acquireTimeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        IOracleMigrationLockSession? session = null;
        try
        {
            session = await operations.OpenSessionAsync(
                    connectionString,
                    targetUserName,
                    operationSource.Token)
                .ConfigureAwait(false);
            var lockName = OracleMigrationLockName.Derive(serviceId);
            var lockHandle = await session.AllocateLockHandleAsync(
                    lockName,
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var requestTimeoutSeconds = GetRemainingTimeoutSeconds(acquireTimeout, elapsed.Elapsed);
            var resultCode = await session.RequestLockAsync(
                    lockHandle,
                    requestTimeoutSeconds,
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (resultCode == 1 || timeoutSource.IsCancellationRequested)
            {
                throw Failure(
                    WellKnownMigrationErrorCodes.LockTimeout,
                    "Oracle migration lock acquisition timed out.");
            }

            if (resultCode != 0)
            {
                throw Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Oracle DBMS_LOCK.REQUEST did not acquire the migration lock.");
            }

            var lease = new OracleMigrationLock(session, lockHandle);
            session = null;
            return lease;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
            {
                throw Failure(
                    WellKnownMigrationErrorCodes.LockTimeout,
                    "Oracle migration lock acquisition timed out.");
            }

            throw Failure(
                WellKnownMigrationErrorCodes.LockFailed,
                "Oracle migration lock acquisition failed.");
        }
        catch (OracleMigrationLockOperationException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception.Kind == OracleMigrationLockFailureKind.NotSupported
                ? Failure(
                    WellKnownMigrationErrorCodes.LockNotSupported,
                    "The Oracle target user cannot execute SYS.DBMS_LOCK directly.")
                : Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "Oracle migration lock acquisition failed.");
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

    private static int GetRemainingTimeoutSeconds(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw Failure(
                WellKnownMigrationErrorCodes.LockTimeout,
                "Oracle migration lock acquisition timed out.");
        }

        return (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1D, int.MaxValue);
    }

    private static bool TryBuildTarget(
        BootstrapDatabaseConfiguration bootstrap,
        out OracleConnectionStringBuilder connectionString,
        out string targetUserName)
    {
        connectionString = null!;
        targetUserName = string.Empty;
        if (!string.Equals(
                bootstrap.Provider,
                WellKnownDatabaseProviderIds.Oracle,
                StringComparison.OrdinalIgnoreCase) ||
            !OracleDatabaseTarget.TryNormalizeServerVersion(
                bootstrap.ServerVersion,
                out var majorVersion) ||
            majorVersion < OracleDatabaseTarget.MinimumSupportedServerMajorVersion ||
            !OracleDatabaseTarget.TryBuildConnectionString(
                bootstrap.ConnectionString,
                out connectionString) ||
            !OracleDatabaseTarget.TryGetTargetIdentity(
                connectionString,
                out targetUserName,
                out _,
                out _))
        {
            return false;
        }

        connectionString.Pooling = false;
        connectionString["Enlist"] = "false";
        OracleDatabaseTarget.ApplySafeTimeout(connectionString);
        return true;
    }

    private static DatabaseMigrationLockException Failure(string errorCode, string message) =>
        new(errorCode, message);
}

internal sealed class OracleMigrationLock : IDatabaseMigrationLock
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Conservative running-process deadline covering the probe interval, one-second command
    /// timeout, cancellation propagation, and scheduling margin.
    /// </summary>
    internal static TimeSpan LeaseLossDetectionBound { get; } = TimeSpan.FromSeconds(5);

    private readonly IOracleMigrationLockSession session;
    private readonly string lockHandle;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly CancellationTokenSource leaseLost = new();
    private readonly Task monitorTask;
    private int disposeStarted;

    internal OracleMigrationLock(IOracleMigrationLockSession session, string lockHandle)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockHandle);
        this.session = session;
        this.lockHandle = lockHandle;
        monitorTask = MonitorSessionAsync();
    }

    public string ProviderId => WellKnownDatabaseProviderIds.Oracle;

    public CancellationToken LeaseLost => leaseLost.Token;

    internal long SessionId => session.SessionId;

    /// <summary>Stops monitoring, explicitly releases the named lock, and closes the session.</summary>
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
            if (!leaseLost.IsCancellationRequested)
            {
                try
                {
                    await session.ReleaseLockAsync(lockHandle, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Closing the session remains the authoritative release fallback.
                }
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

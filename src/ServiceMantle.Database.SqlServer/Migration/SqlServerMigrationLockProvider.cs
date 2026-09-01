using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.SqlServer.Migration;

/// <summary>
/// Provides an exclusive SQL Server application lock backed by <c>sys.sp_getapplock</c> on a
/// dedicated, unpooled target-database session.
/// </summary>
public sealed class SqlServerMigrationLockProvider : IDatabaseMigrationLockProvider
{
    private static readonly TimeSpan MaximumAcquireTimeout =
        TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly ISqlServerMigrationLockOperations operations;

    /// <summary>Initializes the provider with real Microsoft.Data.SqlClient operations.</summary>
    public SqlServerMigrationLockProvider()
        : this(new SqlServerMigrationLockOperations())
    {
    }

    internal SqlServerMigrationLockProvider(ISqlServerMigrationLockOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    /// <summary>Gets the canonical SQL Server provider ID.</summary>
    public string ProviderId => WellKnownDatabaseProviderIds.SqlServer;

    /// <summary>
    /// Acquires a session-owned application lock within one timeout shared by connection,
    /// target-identity validation, and <c>sys.sp_getapplock</c>.
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
            throw Failure(
                WellKnownMigrationErrorCodes.LockFailed,
                "The SQL Server migration lock target is invalid or unsupported.");
        }

        var resourceName = SqlServerMigrationLockName.Derive(serviceId);
        var elapsed = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(acquireTimeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        ISqlServerMigrationLockSession? session = null;
        try
        {
            var commandTimeoutSeconds = GetCommandTimeoutSeconds(
                GetRemaining(acquireTimeout, elapsed.Elapsed));
            session = await operations.OpenSessionAsync(
                    connectionString,
                    databaseName,
                    commandTimeoutSeconds,
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = GetRemaining(acquireTimeout, elapsed.Elapsed);
            var resultCode = await session.AcquireLockAsync(
                    resourceName,
                    GetLockTimeoutMilliseconds(remaining),
                    GetCommandTimeoutSeconds(remaining),
                    operationSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (timeoutSource.IsCancellationRequested || resultCode == -1)
            {
                throw TimeoutFailure();
            }

            if (resultCode is not 0 and not 1)
            {
                throw Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "SQL Server did not acquire the migration application lock.");
            }

            var lease = new SqlServerMigrationLock(session, resourceName);
            session = null;
            return lease;
        }
        catch (DatabaseMigrationLockException)
        {
            ThrowCallerCancellation(cancellationToken);
            throw;
        }
        catch (SqlServerMigrationLockOperationException exception)
        {
            ThrowCallerCancellation(cancellationToken);
            if (timeoutSource.IsCancellationRequested)
            {
                throw TimeoutFailure();
            }

            throw exception.Kind == SqlServerMigrationLockFailureKind.NotSupported
                ? Failure(
                    WellKnownMigrationErrorCodes.LockNotSupported,
                    "The SQL Server target cannot execute session application locks.")
                : Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "SQL Server migration lock acquisition failed.");
        }
        catch (OperationCanceledException)
        {
            ThrowCallerCancellation(cancellationToken);
            throw timeoutSource.IsCancellationRequested
                ? TimeoutFailure()
                : Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "SQL Server migration lock acquisition failed.");
        }
        catch (Exception)
        {
            ThrowCallerCancellation(cancellationToken);
            throw timeoutSource.IsCancellationRequested
                ? TimeoutFailure()
                : Failure(
                    WellKnownMigrationErrorCodes.LockFailed,
                    "SQL Server migration lock acquisition failed.");
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
        out SqlConnectionStringBuilder connectionString,
        out string databaseName)
    {
        connectionString = null!;
        databaseName = string.Empty;
        if (!string.Equals(
                bootstrap.Provider,
                WellKnownDatabaseProviderIds.SqlServer,
                StringComparison.OrdinalIgnoreCase) ||
            !IsSupportedServerVersion(bootstrap.ServerVersion) ||
            !SqlServerDatabaseTarget.TryBuildConnectionString(
                bootstrap.ConnectionString,
                out connectionString) ||
            !SqlServerDatabaseTarget.TryGetValidDatabaseName(connectionString, out databaseName))
        {
            return false;
        }

        connectionString.Pooling = false;
        connectionString.Enlist = false;
        connectionString.ConnectRetryCount = 0;
        SqlServerDatabaseTarget.ApplySafeTimeouts(connectionString);
        return true;
    }

    private static bool IsSupportedServerVersion(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        var parts = serverVersion.Trim().Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(part =>
                part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        return int.Parse(parts[0], CultureInfo.InvariantCulture) >=
            SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion;
    }

    private static TimeSpan GetRemaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : throw TimeoutFailure();
    }

    private static int GetLockTimeoutMilliseconds(TimeSpan remaining) =>
        (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1D, int.MaxValue);

    private static int GetCommandTimeoutSeconds(TimeSpan remaining) =>
        (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds) + 1D, 1D, int.MaxValue);

    private static void ThrowCallerCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "SQL Server migration lock acquisition was cancelled by the caller.",
                cancellationToken);
        }
    }

    private static DatabaseMigrationLockException TimeoutFailure() => new(
        WellKnownMigrationErrorCodes.LockTimeout,
        "SQL Server migration lock acquisition timed out.");

    private static DatabaseMigrationLockException Failure(string errorCode, string message) =>
        new(errorCode, message);
}

internal sealed class SqlServerMigrationLock : IDatabaseMigrationLock
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan LeaseLossDetectionBound { get; } = TimeSpan.FromSeconds(5);

    private readonly ISqlServerMigrationLockSession session;
    private readonly string resourceName;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly CancellationTokenSource leaseLost = new();
    private readonly Task monitorTask;
    private int disposeStarted;

    internal SqlServerMigrationLock(
        ISqlServerMigrationLockSession session,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        this.session = session;
        this.resourceName = resourceName;
        monitorTask = MonitorSessionAsync();
    }

    public string ProviderId => WellKnownDatabaseProviderIds.SqlServer;

    public CancellationToken LeaseLost => leaseLost.Token;

    internal int SessionId => session.SessionId;

    /// <summary>Stops monitoring, releases the application lock once, and closes the session.</summary>
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
                await session.ReleaseLockAsync(resourceName, CancellationToken.None)
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

                    await session.ProbeLeaseAsync(resourceName, monitorCancellation.Token)
                        .ConfigureAwait(false);
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

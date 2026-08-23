using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Test double for IDatabaseMigrationLockProvider with configurable behavior.
/// Supports actual lock semantics when needed for concurrent testing via shared semaphores.
/// </summary>
internal sealed class FakeMigrationLockProvider : IDatabaseMigrationLockProvider
{
    // Shared semaphores indexed by (ProviderId, ServiceId) for multi-instance simulation
    private static readonly Dictionary<(string, string), SemaphoreSlim> SharedLocks = [];
    private static readonly object LockDictLock = new();

    private readonly Exception? acquireException;
    private readonly bool returnNullLease;
    private int leaseDisposeCount;

    public string ProviderId { get; }
    public int AcquireAttempts { get; private set; }

    /// <summary>
    /// Number of leases returned by this provider that have completed their first DisposeAsync call.
    /// Repeated DisposeAsync calls on the same lease do not increment this counter, and a null
    /// lease (returnNullLease) never contributes to it.
    /// </summary>
    public int LeaseDisposeCount => leaseDisposeCount;

    public FakeMigrationLockProvider(
        string providerId = "PostgreSQL",
        Exception? acquireException = null,
        bool returnNullLease = false)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        this.acquireException = acquireException;
        this.returnNullLease = returnNullLease;
    }

    public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken cancellationToken = default)
    {
        AcquireAttempts++;
        cancellationToken.ThrowIfCancellationRequested();

        if (acquireException is not null)
        {
            throw acquireException;
        }

        if (returnNullLease)
        {
            return null!;
        }

        // Acquire shared semaphore for this lock key
        var lockKey = (ProviderId, serviceId.Value);
        SemaphoreSlim semaphore;

        lock (LockDictLock)
        {
            if (!SharedLocks.TryGetValue(lockKey, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                SharedLocks[lockKey] = semaphore;
            }
        }

        // Wait for lock with timeout
        using var cts = new CancellationTokenSource(acquireTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        try
        {
            await semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cts.Token.IsCancellationRequested)
            {
                throw new DatabaseMigrationLockException(
                    WellKnownMigrationErrorCodes.LockTimeout,
                    "Lock acquisition timed out.");
            }

            throw;
        }

        IDatabaseMigrationLock fakeLock = new FakeMigrationLock(
            ProviderId,
            semaphore,
            () => Interlocked.Increment(ref leaseDisposeCount));
        return fakeLock;
    }

    private sealed class FakeMigrationLock : IDatabaseMigrationLock
    {
        private int disposed;
        private readonly SemaphoreSlim? semaphore;
        private readonly Action? onFirstDispose;

        public string ProviderId { get; }

        public FakeMigrationLock(string providerId, SemaphoreSlim? semaphore, Action? onFirstDispose)
        {
            ProviderId = providerId;
            this.semaphore = semaphore;
            this.onFirstDispose = onFirstDispose;
        }

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange guards against double-counting a repeated DisposeAsync call.
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                onFirstDispose?.Invoke();
                semaphore?.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

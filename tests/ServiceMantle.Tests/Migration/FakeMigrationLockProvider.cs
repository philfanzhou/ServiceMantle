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
    private readonly Func<CancellationToken, Task>? acquireDelay;
    private readonly bool ignoreCancellationAfterAcquireDelay;
    private readonly Exception? disposeException;
    private CancellationTokenSource? activeLeaseLoss;
    private int leaseDisposeCount;

    public string ProviderId { get; }
    public int AcquireAttempts { get; private set; }

    /// <summary>
    /// Number of leases returned by this provider that have completed their first DisposeAsync call.
    /// Repeated DisposeAsync calls on the same lease do not increment this counter, and a null
    /// lease (returnNullLease) never contributes to it.
    /// </summary>
    public int LeaseDisposeCount => leaseDisposeCount;

    public void LoseLease() => activeLeaseLoss?.Cancel();

    public FakeMigrationLockProvider(
        string providerId = "PostgreSQL",
        Exception? acquireException = null,
        bool returnNullLease = false,
        Func<CancellationToken, Task>? acquireDelay = null,
        bool ignoreCancellationAfterAcquireDelay = false,
        Exception? disposeException = null)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        this.acquireException = acquireException;
        this.returnNullLease = returnNullLease;
        this.acquireDelay = acquireDelay;
        this.ignoreCancellationAfterAcquireDelay = ignoreCancellationAfterAcquireDelay;
        this.disposeException = disposeException;
    }

    public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken cancellationToken = default)
    {
        AcquireAttempts++;
        cancellationToken.ThrowIfCancellationRequested();

        if (acquireDelay is not null)
        {
            await acquireDelay(cancellationToken).ConfigureAwait(false);
        }

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
        using var linkedCts = ignoreCancellationAfterAcquireDelay
            ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

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

        activeLeaseLoss = new CancellationTokenSource();
        IDatabaseMigrationLock fakeLock = new FakeMigrationLock(
            ProviderId,
            semaphore,
            activeLeaseLoss.Token,
            () => Interlocked.Increment(ref leaseDisposeCount),
            disposeException);
        return fakeLock;
    }

    private sealed class FakeMigrationLock : IDatabaseMigrationLock
    {
        private int disposed;
        private readonly SemaphoreSlim? semaphore;
        private readonly CancellationToken leaseLost;
        private readonly Action? onFirstDispose;
        private readonly Exception? disposeException;

        public string ProviderId { get; }

        public FakeMigrationLock(
            string providerId,
            SemaphoreSlim? semaphore,
            CancellationToken leaseLost,
            Action? onFirstDispose,
            Exception? disposeException)
        {
            ProviderId = providerId;
            this.semaphore = semaphore;
            this.leaseLost = leaseLost;
            this.onFirstDispose = onFirstDispose;
            this.disposeException = disposeException;
        }

        public CancellationToken LeaseLost => leaseLost;

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange guards against double-counting a repeated DisposeAsync call.
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                onFirstDispose?.Invoke();
                semaphore?.Release();
                if (disposeException is not null)
                {
                    throw disposeException;
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}

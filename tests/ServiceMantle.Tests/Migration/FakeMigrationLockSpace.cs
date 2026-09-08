namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Explicit lock space shared by <see cref="FakeMigrationLockProvider"/> instances that are meant to
/// contend with each other. A provider that is not handed a space creates its own, so contention is
/// always scoped to what a single test constructed instead of to the whole test assembly.
/// </summary>
internal sealed class FakeMigrationLockSpace
{
    private readonly Dictionary<(string ProviderId, string ServiceId), SemaphoreSlim> locks = [];
    private readonly object gate = new();

    public SemaphoreSlim GetOrAdd(string providerId, string serviceId)
    {
        var key = (providerId, serviceId);

        lock (gate)
        {
            if (!locks.TryGetValue(key, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                locks[key] = semaphore;
            }

            return semaphore;
        }
    }
}

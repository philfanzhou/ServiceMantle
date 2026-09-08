using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Pins the contention scope of <see cref="FakeMigrationLockProvider"/>: a test only ever competes
/// with the providers it constructed, never with providers built by another test running in parallel.
/// </summary>
public class FakeMigrationLockSpaceTests
{
    private static readonly ServiceId SharedServiceId = ServiceId.Parse("shared-service");

    private static readonly BootstrapDatabaseConfiguration Bootstrap =
        new("PostgreSQL", "15", "Host=localhost;Database=test;Username=user;Password=pass");

    [Fact]
    public async Task Separate_lock_spaces_do_not_block_the_same_provider_and_service_key()
    {
        var first = new FakeMigrationLockProvider();
        var second = new FakeMigrationLockProvider();

        await using var firstLease = await first.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        // Same (ProviderId, ServiceId) as the lease above, different space: it must not wait for it.
        await using var secondLease = await second.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstLease);
        Assert.NotNull(secondLease);
        Assert.Equal("PostgreSQL", firstLease.ProviderId);
        Assert.Equal("PostgreSQL", secondLease.ProviderId);
    }

    [Fact]
    public async Task An_undisposed_lease_in_one_space_does_not_reach_another_space()
    {
        // Deliberately never disposed: the lease stays held for the rest of this test.
        var abandonedSpace = new FakeMigrationLockSpace();
        var abandonedProvider = new FakeMigrationLockProvider(lockSpace: abandonedSpace);
        var abandonedLease = await abandonedProvider.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(abandonedLease);

        var otherProvider = new FakeMigrationLockProvider(lockSpace: new FakeMigrationLockSpace());
        await using var otherLease = await otherProvider.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromMilliseconds(500),
            TestContext.Current.CancellationToken);

        Assert.NotNull(otherLease);
        Assert.Equal(0, abandonedProvider.LeaseDisposeCount);
    }

    [Fact]
    public async Task A_shared_lock_space_still_serialises_the_same_key()
    {
        var space = new FakeMigrationLockSpace();
        var holder = new FakeMigrationLockProvider(lockSpace: space);
        var waiter = new FakeMigrationLockProvider(lockSpace: space);

        var heldLease = await holder.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var waiting = waiter.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(waiting.IsCompleted);

        await heldLease.DisposeAsync();

        await using var acquired = await waiting;
        Assert.NotNull(acquired);
        Assert.Equal(1, holder.LeaseDisposeCount);
    }

    [Fact]
    public async Task Waiting_past_the_acquire_timeout_inside_one_space_reports_LockTimeout()
    {
        var space = new FakeMigrationLockSpace();
        var holder = new FakeMigrationLockProvider(lockSpace: space);
        var waiter = new FakeMigrationLockProvider(lockSpace: space);

        // Held for the duration of the test so the waiter can only leave through its timeout.
        await holder.AcquireAsync(
            SharedServiceId,
            Bootstrap,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(
            () => waiter.AcquireAsync(
                SharedServiceId,
                Bootstrap,
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, exception.ErrorCode);
    }
}

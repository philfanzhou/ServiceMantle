using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public class DatabaseMigrationLockProviderRegistryTests
{
    [Fact]
    public void Constructor_WithNullEnumerable_AllowsEmptyRegistry()
    {
        var registry = new DatabaseMigrationLockProviderRegistry(null, DatabaseProviderIdResolver.Empty);
        Assert.NotNull(registry);
        Assert.False(registry.TryGetProvider("PostgreSQL", out _));
    }

    [Fact]
    public void Constructor_WithEmptyEnumerable_CreatesEmptyRegistry()
    {
        var registry = new DatabaseMigrationLockProviderRegistry([], DatabaseProviderIdResolver.Empty);
        Assert.NotNull(registry);
        Assert.False(registry.TryGetProvider("PostgreSQL", out _));
    }

    [Fact]
    public void Constructor_WithValidProviders_RegistersThem()
    {
        var lockProvider = new FakeMigrationLockProvider("PostgreSQL");
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

        Assert.True(registry.TryGetProvider("PostgreSQL", out var retrieved));
        Assert.NotNull(retrieved);
    }

    [Fact]
    public void TryGetProvider_IsCaseInsensitive()
    {
        var lockProvider = new FakeMigrationLockProvider("PostgreSQL");
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

        Assert.True(registry.TryGetProvider("postgresql", out var retrieved));
        Assert.NotNull(retrieved);
    }

    [Fact]
    public void TryGetProvider_WithNullProviders_RejectsDuplicateIds()
    {
        var provider1 = new FakeMigrationLockProvider("PostgreSQL");
        var provider2 = new FakeMigrationLockProvider("PostgreSQL");

        var ex = Assert.Throws<ArgumentException>(
            () => new DatabaseMigrationLockProviderRegistry([provider1, provider2], DatabaseProviderIdResolver.Empty));

        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void TryGetProvider_WithNullProvider_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DatabaseMigrationLockProviderRegistry([null!], DatabaseProviderIdResolver.Empty));

        Assert.NotNull(ex);
    }

    [Fact]
    public void TryGetProvider_WithWhitespaceProviderId_ThrowsArgumentException()
    {
        var provider = new BadMigrationLockProvider("  ");

        var ex = Assert.Throws<ArgumentException>(
            () => new DatabaseMigrationLockProviderRegistry([provider], DatabaseProviderIdResolver.Empty));

        Assert.NotNull(ex);
    }

    [Fact]
    public void TryGetProvider_NotFound_ReturnsFalse()
    {
        var lockProvider = new FakeMigrationLockProvider("PostgreSQL");
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

        var found = registry.TryGetProvider("MySQL", out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void TryGetProvider_WithNullId_ReturnsFalse()
    {
        var lockProvider = new FakeMigrationLockProvider("PostgreSQL");
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

        var found = registry.TryGetProvider(null, out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void TryGetProvider_WithEmptyId_ReturnsFalse()
    {
        var lockProvider = new FakeMigrationLockProvider("PostgreSQL");
        var registry = new DatabaseMigrationLockProviderRegistry([lockProvider], DatabaseProviderIdResolver.Empty);

        var found = registry.TryGetProvider("", out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    private sealed class BadMigrationLockProvider : IDatabaseMigrationLockProvider
    {
        public string ProviderId { get; }

        public BadMigrationLockProvider(string providerId)
        {
            ProviderId = providerId;
        }

        public ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}

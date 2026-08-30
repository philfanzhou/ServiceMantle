using Microsoft.EntityFrameworkCore;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL concurrency coverage for the installation store. These tests are enabled by
/// RUN_SERVICEMANTLE_POSTGRES_TESTS=true, matching the existing container-test convention.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlServiceInstallationStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_installation")
            .WithUsername("test-user")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        connectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrent_pending_inserts_are_idempotent_on_real_PostgreSql()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var options = new DbContextOptionsBuilder<PostgreSqlInstallationDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaContext = new PostgreSqlInstallationDbContext(options))
        {
            await schemaContext.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
            await schemaContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var reachedSaveA = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedSaveB = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSaves = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var contextA = new PostgreSqlInstallationDbContext(
            options,
            async cancellationToken =>
            {
                reachedSaveA.SetResult();
                await releaseSaves.Task.WaitAsync(cancellationToken);
            });
        await using var contextB = new PostgreSqlInstallationDbContext(
            options,
            async cancellationToken =>
            {
                reachedSaveB.SetResult();
                await releaseSaves.Task.WaitAsync(cancellationToken);
            });

        var serviceId = ServiceId.Parse("signacore");
        var storeA = new EfCoreServiceInstallationStore<PostgreSqlInstallationDbContext>(contextA);
        var storeB = new EfCoreServiceInstallationStore<PostgreSqlInstallationDbContext>(contextB);
        var createA = storeA.CreatePendingAsync(
            serviceId,
            TestContext.Current.CancellationToken).AsTask();
        var createB = storeB.CreatePendingAsync(
            serviceId,
            TestContext.Current.CancellationToken).AsTask();

        try
        {
            await Task.WhenAll(reachedSaveA.Task, reachedSaveB.Task)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseSaves.TrySetResult();
        }

        var results = await Task.WhenAll(createA, createB)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal(InstallationStatus.PendingSetup, result.Status));
        Assert.DoesNotContain(
            contextA.ChangeTracker.Entries<ServiceInstallationEntity>(),
            entry => entry.State == EntityState.Added);
        Assert.DoesNotContain(
            contextB.ChangeTracker.Entries<ServiceInstallationEntity>(),
            entry => entry.State == EntityState.Added);

        await using var verification = new PostgreSqlInstallationDbContext(options);
        Assert.Equal(
            1,
            await verification.ServiceInstallations.AsNoTracking().CountAsync(
                item => item.ServiceId == serviceId.Value,
                TestContext.Current.CancellationToken));
    }

    private static bool ShouldRunPostgreSqlTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    private sealed class PostgreSqlInstallationDbContext(
        DbContextOptions<PostgreSqlInstallationDbContext> options,
        Func<CancellationToken, Task>? beforeSaveChangesAsync = null)
        : DbContext(options), IServiceMantleDbContext
    {
        private Func<CancellationToken, Task>? beforeSaveChangesAsync = beforeSaveChangesAsync;

        public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

        public override async Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            if (beforeSaveChangesAsync is { } callback)
            {
                beforeSaveChangesAsync = null;
                await callback(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleInstallation();
    }
}

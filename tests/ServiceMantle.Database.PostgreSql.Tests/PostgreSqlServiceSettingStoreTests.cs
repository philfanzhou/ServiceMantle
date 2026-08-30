using Microsoft.EntityFrameworkCore;
using ServiceMantle.Configuration;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>Real PostgreSQL optimistic-concurrency coverage for shared service settings.</summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlServiceSettingStoreTests : IAsyncLifetime
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
            .WithDatabase("servicemantle_settings")
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
    public async Task Concurrent_updates_have_one_winner_and_one_version_conflict()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var options = new DbContextOptionsBuilder<PostgreSqlSettingDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaContext = new PostgreSqlSettingDbContext(options))
        {
            await schemaContext.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
            await schemaContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var serviceId = ServiceId.Parse("signacore");
        var initialStore = new EfCoreServiceSettingStore<PostgreSqlSettingDbContext>(
            new SettingDbContextFactory(options));
        var initial = await initialStore.UpdateAsync(
            serviceId,
            Update(0, "initial"),
            TestContext.Current.CancellationToken);
        Assert.True(initial.Succeeded);

        var reachedSaveA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedSaveB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSaves = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeA = new EfCoreServiceSettingStore<PostgreSqlSettingDbContext>(
            new SettingDbContextFactory(
                options,
                async cancellationToken =>
                {
                    reachedSaveA.SetResult();
                    await releaseSaves.Task.WaitAsync(cancellationToken);
                }));
        var storeB = new EfCoreServiceSettingStore<PostgreSqlSettingDbContext>(
            new SettingDbContextFactory(
                options,
                async cancellationToken =>
                {
                    reachedSaveB.SetResult();
                    await releaseSaves.Task.WaitAsync(cancellationToken);
                }));

        var updateA = storeA.UpdateAsync(
            serviceId,
            Update(1, "actor-a"),
            TestContext.Current.CancellationToken).AsTask();
        var updateB = storeB.UpdateAsync(
            serviceId,
            Update(1, "actor-b"),
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

        var results = await Task.WhenAll(updateA, updateB)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var snapshot = await initialStore.LoadAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.Single(results, result => result.Succeeded && result.Version == 2);
        Assert.Single(results, result =>
            !result.Succeeded &&
            result.ErrorCode == WellKnownServiceSettingStoreErrorCodes.VersionConflict);
        Assert.Equal(2, snapshot.Version);
        Assert.Contains(snapshot.Values["worker.owner"], new[] { "actor-a", "actor-b" });
    }

    private static ServiceSettingStoreUpdate Update(long expectedVersion, string value) =>
        new(
            expectedVersion,
            new Dictionary<string, string?> { ["worker.owner"] = value },
            updatedBy: value,
            restartRequired: false);

    private static bool ShouldRunPostgreSqlTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    private sealed class SettingDbContextFactory(
        DbContextOptions<PostgreSqlSettingDbContext> options,
        Func<CancellationToken, Task>? beforeSave = null)
        : IDbContextFactory<PostgreSqlSettingDbContext>
    {
        public PostgreSqlSettingDbContext CreateDbContext() => new(options, beforeSave);

        public ValueTask<PostgreSqlSettingDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateDbContext());
        }
    }

    private sealed class PostgreSqlSettingDbContext(
        DbContextOptions<PostgreSqlSettingDbContext> options,
        Func<CancellationToken, Task>? beforeSave = null)
        : DbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (beforeSave is not null)
            {
                await beforeSave(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleSettings();
    }
}

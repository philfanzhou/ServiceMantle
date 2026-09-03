using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Configuration;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;
using static ServiceMantle.Persistence.EntityFrameworkCore.Tests.ServiceSettingUpdateTests;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerServiceSettingUpdateTests
{
    [Fact]
    public async Task Concurrent_insert_and_update_have_one_winner_and_no_loser_audit_then_fresh_retry_succeeds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_SQLSERVER_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true, "SQL Server integration tests disabled.");
        var token = TestContext.Current.CancellationToken;
        await using var container = new MsSqlBuilder(Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLSERVER_IMAGE")
            ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await container.StartAsync(token);
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = "setting_batch_concurrency"
        }.ConnectionString;
        UpdateContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<UpdateContext>()
            .UseSqlServer(connectionString).AddInterceptors(interceptors).Options);
        await using (var schema = Context()) await schema.Database.EnsureCreatedAsync(token);

        foreach (var initialVersion in new long[] { 0, 1 })
        {
            await using (var reset = Context())
            {
                await reset.Set<ManagementAuditLogEntity>().ExecuteDeleteAsync(token);
                await reset.Set<ServiceSettingEntity>().ExecuteDeleteAsync(token);
                if (initialVersion == 1)
                {
                    await using var seedTransaction = await reset.Database.BeginTransactionAsync(token);
                    Assert.True((await Updater(reset).UpdateAsync(Command(0, ("count", "1")), token)).Succeeded);
                    await seedTransaction.CommitAsync(token);
                }
            }
            var gate = new BeforeSaveGate();
            await using var loser = Context(gate);
            await using var loserTransaction = await loser.Database.BeginTransactionAsync(token);
            var pending = Updater(loser).UpdateAsync(Command(initialVersion, ("count", "3")), token).AsTask();
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            try
            {
                await using var winner = Context();
                await using var winnerTransaction = await winner.Database.BeginTransactionAsync(token);
                Assert.True((await Updater(winner).UpdateAsync(Command(initialVersion, ("enabled", "true")), token)).Succeeded);
                await winnerTransaction.CommitAsync(token);
            }
            finally
            {
                gate.Release.TrySetResult();
            }
            Assert.Equal(ServiceSettingUpdateStatus.VersionConflict, (await pending).Status);
            Assert.Empty(loser.ChangeTracker.Entries());
            await loserTransaction.RollbackAsync(token);
            await using var observer = Context();
            var persisted = await observer.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(token);
            Assert.Equal(initialVersion + 1, persisted.Version);
            Assert.Equal(initialVersion + 1, await observer.Set<ManagementAuditLogEntity>().CountAsync(token));
            Assert.DoesNotContain("3", persisted.ValuesJson, StringComparison.Ordinal);
            await using var retryTransaction = await observer.Database.BeginTransactionAsync(token);
            Assert.True((await Updater(observer).UpdateAsync(Command(persisted.Version, ("count", "3")), token)).Succeeded);
            await retryTransaction.CommitAsync(token);
            var final = await observer.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(token);
            Assert.Contains("true", final.ValuesJson, StringComparison.Ordinal);
            Assert.Contains("3", final.ValuesJson, StringComparison.Ordinal);
            Assert.Equal(initialVersion + 2, final.Version);
            Assert.Equal(initialVersion + 2, await observer.Set<ManagementAuditLogEntity>().CountAsync(token));
        }
    }

    private sealed class BeforeSaveGate : SaveChangesInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }
}

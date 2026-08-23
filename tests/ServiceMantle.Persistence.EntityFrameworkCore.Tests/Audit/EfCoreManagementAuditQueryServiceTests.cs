using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

public sealed class EfCoreManagementAuditQueryServiceTests
{
    private static readonly ManagementAuditTarget ServiceTarget =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "signacore");
    private static readonly ManagementAuditTarget ConfigTarget =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp");

    [Fact]
    public async Task QueryAsync_filters_by_action()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(action: WellKnownManagementAuditActions.ConfigurationChanged),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("configuration.changed", result.Items.Single().Action.Value);
    }

    [Fact]
    public async Task QueryAsync_filters_by_target_type_and_target_id()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(
                targetType: WellKnownManagementAuditTargetTypes.Configuration, targetId: "smtp"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("smtp", result.Items.Single().Target.Id);
    }

    [Fact]
    public async Task QueryAsync_filters_by_operator_id()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-2", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(operatorId: "admin-2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("admin-2", result.Items.Single().Operator.OperatorId);
    }

    [Fact]
    public async Task QueryAsync_filters_by_time_range()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(5)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(10)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(fromUtc: Day(4), toUtc: Day(6)),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(Day(5), result.Items.Single().OccurredAtUtc);
    }

    [Fact]
    public async Task QueryAsync_orders_newest_first_by_default()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken);

        Assert.Equal([Day(3), Day(2), Day(1)], result.Items.Select(item => item.OccurredAtUtc));
    }

    [Fact]
    public async Task QueryAsync_orders_oldest_first_when_requested()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(sortOrder: ManagementAuditSortOrder.Oldest),
            TestContext.Current.CancellationToken);

        Assert.Equal([Day(1), Day(2), Day(3)], result.Items.Select(item => item.OccurredAtUtc));
    }

    [Fact]
    public async Task QueryAsync_paginates_stably_across_pages()
    {
        var records = Enumerable.Range(0, 5)
            .Select(index => Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(index)))
            .ToArray();
        await using var context = await SeedAsync(records);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 1, pageSize: 2), TestContext.Current.CancellationToken);
        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 2), TestContext.Current.CancellationToken);
        var thirdPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 3, pageSize: 2), TestContext.Current.CancellationToken);

        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Single(thirdPage.Items);
        Assert.True(firstPage.HasNextPage);
        Assert.True(secondPage.HasNextPage);
        Assert.False(thirdPage.HasNextPage);

        var seenIds = firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items)
            .Select(item => item.Id)
            .ToList();
        Assert.Equal(5, seenIds.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_uses_id_as_stable_tiebreaker_for_equal_timestamps()
    {
        var sameInstant = Day(1);
        var records = Enumerable.Range(0, 4)
            .Select(_ => Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, sameInstant))
            .ToArray();
        await using var context = await SeedAsync(records);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 1, pageSize: 2), TestContext.Current.CancellationToken);
        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 2), TestContext.Current.CancellationToken);

        var seenIds = firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).ToList();
        Assert.Equal(4, seenIds.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_never_returns_secrets_that_were_redacted_at_write_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new AuditTestDbContext(
            new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, "admin-1"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ConfigTarget,
            securityDescription: "Updated Server=db;Password=super-secret; for smtp relay.");
        await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken);

        var description = result.Items.Single().SecurityDescription;
        Assert.DoesNotContain("super-secret", description);
        Assert.Contains("[REDACTED]", description, StringComparison.Ordinal);
    }

    private static DateTimeOffset Day(int offset) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(offset);

    private static ManagementAuditEvent Record(
        string operatorId,
        ManagementAuditAction action,
        ManagementAuditTarget target,
        DateTimeOffset occurredAtUtc) =>
        ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, operatorId),
            action,
            target,
            occurredAtUtc: occurredAtUtc);

    private static async Task<AuditTestDbContext> SeedAsync(params ManagementAuditEvent[] events)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var context = new AuditTestDbContext(
            new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        foreach (var auditEvent in events)
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return context;
    }
}

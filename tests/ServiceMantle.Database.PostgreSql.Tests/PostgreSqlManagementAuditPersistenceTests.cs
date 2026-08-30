using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL persistence coverage for the management audit model. These tests are enabled by
/// RUN_SERVICEMANTLE_POSTGRES_TESTS=true, matching the existing container-test convention.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlManagementAuditPersistenceTests : IAsyncLifetime
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
            .WithDatabase("servicemantle_audit")
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
    public async Task PostgreSql_round_trips_audit_event_and_applies_utc_filters_and_sorting()
    {
        await using var context = await CreateResetContextAsync();
        var target = ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp");
        var writer = new EfCoreManagementAuditWriter<PostgreSqlAuditDbContext>(context);

        await writer.RecordAsync(Event("admin-1", target, Day(1)), TestContext.Current.CancellationToken);
        await writer.RecordAsync(Event("admin-2", target, Day(2)), TestContext.Current.CancellationToken);
        await writer.RecordAsync(Event("admin-1", target, Day(3)), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        var service = new EfCoreManagementAuditQueryService<PostgreSqlAuditDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(
                action: WellKnownManagementAuditActions.ConfigurationChanged,
                targetType: target.Type,
                targetId: target.Id,
                operatorId: "admin-1",
                fromUtc: new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.FromHours(8)),
                toUtc: new DateTimeOffset(2026, 1, 4, 8, 0, 0, TimeSpan.FromHours(8)),
                sortOrder: ManagementAuditSortOrder.Oldest),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal([Day(1), Day(3)], result.Items.Select(item => item.OccurredAtUtc));
        Assert.All(result.Items, item => Assert.Equal("admin-1", item.Operator.OperatorId));
    }

    [Fact]
    public async Task PostgreSql_audit_write_participates_in_caller_transaction_rollback()
    {
        await using var context = await CreateResetContextAsync();
        var writer = new EfCoreManagementAuditWriter<PostgreSqlAuditDbContext>(context);
        var auditEvent = Event(
            "admin-1",
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "signacore"),
            Day(1));

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await CountAuditRowsAsync(context));
    }

    [Fact]
    public async Task PostgreSql_rejects_metadata_exceeding_the_utf8_byte_limit_at_the_database_boundary()
    {
        await using var context = await CreateResetContextAsync();
        var id = Guid.NewGuid().ToString("D");
        var metadataJson = "\"" + string.Concat(Enumerable.Repeat(
            "\U0001F600",
            ((256 * 1024) / 4) + 1)) + "\"";
        var occurredAtUtc = Day(1).UtcDateTime;

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_audit_logs
                    (id, operator_source, action, target_type, target_id, outcome,
                     occurred_at_utc, metadata_json)
                VALUES
                    ({id}, 'system', 'configuration.changed', 'configuration', 'smtp', 1,
                     {occurredAtUtc}, {metadataJson});
                """,
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_service_audit_logs_metadata_json_length", exception.ConstraintName);
    }

    private async Task<PostgreSqlAuditDbContext> CreateResetContextAsync()
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var context = new PostgreSqlAuditDbContext(
            new DbContextOptionsBuilder<PostgreSqlAuditDbContext>()
                .UseNpgsql(connectionString)
                .Options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static ManagementAuditEvent Event(
        string operatorId,
        ManagementAuditTarget target,
        DateTimeOffset occurredAtUtc) =>
        ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId),
            WellKnownManagementAuditActions.ConfigurationChanged,
            target,
            ManagementAuditOutcome.Success,
            occurredAtUtc);

    private static DateTimeOffset Day(int offset) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(offset);

    private static bool ShouldRunPostgreSqlTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    private static async Task<long> CountAuditRowsAsync(PostgreSqlAuditDbContext context)
    {
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM service_audit_logs;";
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class PostgreSqlAuditDbContext(DbContextOptions<PostgreSqlAuditDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.PostgreSql);
    }
}

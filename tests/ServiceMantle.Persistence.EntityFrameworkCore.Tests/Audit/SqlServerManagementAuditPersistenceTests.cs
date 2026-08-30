using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

/// <summary>
/// Real SQL Server coverage for the management audit schema, write transaction boundary, encoded
/// byte constraints, and keyset query path. Enable with RUN_SERVICEMANTLE_SQLSERVER_TESTS=true.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerManagementAuditPersistenceTests : IAsyncLifetime
{
    private const string DatabaseName = "servicemantle_audit";
    private MsSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunSqlServerTests())
        {
            return;
        }

        container = new MsSqlBuilder(GetSqlServerImage()).Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var connectionStringBuilder = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = DatabaseName
        };
        connectionString = connectionStringBuilder.ConnectionString;
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
    public async Task SqlServer_executes_audit_schema_writes_transactions_constraints_and_keyset_queries()
    {
        Assert.SkipUnless(
            ShouldRunSqlServerTests() && connectionString is not null,
            "SQL Server tests disabled or container not initialized.");

        await VerifyRoundTripAndKeysetPaginationAsync();
        await VerifyCallerTransactionRollbackAsync();
        await VerifyMetadataByteConstraintAsync();
    }

    private async Task VerifyRoundTripAndKeysetPaginationAsync()
    {
        await using var context = await CreateResetContextAsync();
        var writer = new EfCoreManagementAuditWriter<SqlServerAuditDbContext>(context);
        var target = ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp");
        var occurredAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var expected = new List<ManagementAuditRecord>();
        for (var index = 1; index <= 3; index++)
        {
            expected.Add(await writer.RecordAsync(
                ManagementAuditEvent.Create(
                    ManagementAuditOperator.Create(
                        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                        $"admin-{index}",
                        $"Administrator {index}"),
                    WellKnownManagementAuditActions.ConfigurationChanged,
                    target,
                    ManagementAuditOutcome.Success,
                    occurredAtUtc,
                    clientIp: "192.0.2.1",
                    correlationId: $"request-{index}",
                    securityDescription: "Updated a non-secret SMTP setting.",
                    metadata: new Dictionary<string, string> { ["setting"] = $"display-name-{index}" }),
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, await CountAuditRowsAsync(context));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var service = new EfCoreManagementAuditQueryService<SqlServerAuditDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(pageSize: 2, sortOrder: ManagementAuditSortOrder.Newest),
            TestContext.Current.CancellationToken);
        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(
                page: 2,
                pageSize: 2,
                sortOrder: ManagementAuditSortOrder.Newest,
                cursor: firstPage.ContinuationCursor),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.ContinuationCursor);
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Single(secondPage.Items);
        Assert.Null(secondPage.ContinuationCursor);

        var actual = firstPage.Items.Concat(secondPage.Items).ToList();
        Assert.Equal(3, actual.Select(item => item.Id).Distinct().Count());
        Assert.Equal(
            expected.Select(item => item.Id).Order(),
            actual.Select(item => item.Id).Order());
        Assert.All(actual, item =>
        {
            Assert.Equal(target, item.Target);
            Assert.Equal(occurredAtUtc, item.OccurredAtUtc);
            Assert.Equal("192.0.2.1", item.ClientIp);
            Assert.Equal("Updated a non-secret SMTP setting.", item.SecurityDescription);
            Assert.StartsWith("request-", item.CorrelationId, StringComparison.Ordinal);
            Assert.StartsWith("display-name-", item.Metadata["setting"], StringComparison.Ordinal);
        });
    }

    private async Task VerifyCallerTransactionRollbackAsync()
    {
        await using var context = await CreateResetContextAsync();
        var writer = new EfCoreManagementAuditWriter<SqlServerAuditDbContext>(context);

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await writer.RecordAsync(
                CreateEvent("admin-rollback", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        context.ChangeTracker.Clear();
        Assert.Equal(0, await CountAuditRowsAsync(context));
    }

    private async Task VerifyMetadataByteConstraintAsync()
    {
        await using var context = await CreateResetContextAsync();
        var metadataJson = "\"" + string.Concat(Enumerable.Repeat(
            "\U0001F600",
            (ManagementAuditEntityMapper.MaxMetadataJsonByteLength / 4) + 1)) + "\"";

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_audit_logs
                    (id, operator_source, action, target_type, target_id, outcome,
                     occurred_at_utc, metadata_json)
                VALUES
                    ({Guid.NewGuid().ToString("D")}, N'system', N'configuration.changed',
                     N'configuration', N'smtp', 1, {DateTime.UtcNow}, {metadataJson});
                """,
                TestContext.Current.CancellationToken));

        Assert.Equal(547, exception.Number);
        Assert.Contains(
            "ck_service_audit_logs_metadata_json_length",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SqlServerAuditDbContext> CreateResetContextAsync()
    {
        Assert.NotNull(connectionString);
        var context = new SqlServerAuditDbContext(
            new DbContextOptionsBuilder<SqlServerAuditDbContext>()
                .UseSqlServer(connectionString)
                .Options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static ManagementAuditEvent CreateEvent(string operatorId, DateTimeOffset occurredAtUtc) =>
        ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
            ManagementAuditOutcome.Success,
            occurredAtUtc);

    private static bool ShouldRunSqlServerTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_SQLSERVER_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetSqlServerImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLSERVER_IMAGE")
            ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private static async Task<long> CountAuditRowsAsync(SqlServerAuditDbContext context)
    {
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM service_audit_logs;";
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class SqlServerAuditDbContext(DbContextOptions<SqlServerAuditDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.SqlServer);
    }
}

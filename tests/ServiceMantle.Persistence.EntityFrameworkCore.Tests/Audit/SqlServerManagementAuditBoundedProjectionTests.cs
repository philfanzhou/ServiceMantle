using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerManagementAuditBoundedProjectionTests : IAsyncLifetime
{
    private const string DatabaseName = "servicemantle_audit_bounded";
    private MsSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRun())
        {
            return;
        }

        container = new MsSqlBuilder(
            Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLSERVER_IMAGE")
                ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = DatabaseName
        }.ConnectionString;
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
    public async Task SqlServer_bounds_an_oversized_nul_prefixed_lookahead_row_in_the_page_sql()
    {
        Assert.SkipUnless(ShouldRun() && connectionString is not null, "SQL Server tests disabled or container unavailable.");
        var observer = new CommandObserver();
        await using var context = new SqlServerAuditDbContext(
            new DbContextOptionsBuilder<SqlServerAuditDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(observer)
                .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE service_audit_logs
                DROP CONSTRAINT ck_service_audit_logs_security_description_length;
            ALTER TABLE service_audit_logs
                ALTER COLUMN security_description nvarchar(max) NULL;
            INSERT INTO service_audit_logs
                (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc,
                 security_description)
            VALUES
                ('10000000-0000-0000-0000-000000000001', N'system', N'configuration.changed',
                 N'configuration', N'smtp', 1, '2026-01-03T00:00:00', N'safe'),
                ('10000000-0000-0000-0000-000000000002', N'system', N'configuration.changed',
                 N'configuration', N'smtp', 1, '2026-01-02T00:00:00',
                 NCHAR(0) + REPLICATE(CAST(N'数' AS nvarchar(max)), 9000));
            """,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<SqlServerAuditDbContext>(context)
                .QueryAsync(
                    ManagementAuditQuery.Create(pageSize: 1),
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.Contains(observer.CommandTexts, sql =>
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("DATALENGTH", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("TOP", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldRun() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_SQLSERVER_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private sealed class SqlServerAuditDbContext(DbContextOptions<SqlServerAuditDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.SqlServer);
    }

    private sealed class CommandObserver : DbCommandInterceptor
    {
        internal List<string> CommandTexts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlManagementAuditBoundedProjectionTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRun())
        {
            return;
        }

        container = new PostgreSqlBuilder(
            Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine")
            .WithDatabase("servicemantle_audit_bounded")
            .WithUsername("test-user")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
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
    public async Task PostgreSql_bounds_an_oversized_multibyte_lookahead_row_in_the_page_sql()
    {
        Assert.SkipUnless(ShouldRun() && container is not null, "PostgreSQL tests disabled or container unavailable.");
        var observer = new CommandObserver();
        await using var context = new PostgreSqlAuditDbContext(
            new DbContextOptionsBuilder<PostgreSqlAuditDbContext>()
                .UseNpgsql(container.GetConnectionString())
                .AddInterceptors(observer)
                .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE service_audit_logs
                DROP CONSTRAINT ck_service_audit_logs_security_description_length;
            ALTER TABLE service_audit_logs
                ALTER COLUMN security_description TYPE text;
            INSERT INTO service_audit_logs
                (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc,
                 security_description)
            VALUES
                ('10000000-0000-0000-0000-000000000001', 'system', 'configuration.changed',
                 'configuration', 'smtp', 1, TIMESTAMP '2026-01-03 00:00:00', 'safe'),
                ('10000000-0000-0000-0000-000000000002', 'system', 'configuration.changed',
                 'configuration', 'smtp', 1, TIMESTAMP '2026-01-02 00:00:00', repeat('数', 6000));
            """,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<PostgreSqlAuditDbContext>(context)
                .QueryAsync(
                    ManagementAuditQuery.Create(pageSize: 1),
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.Contains(observer.CommandTexts, sql =>
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("octet_length", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldRun() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private sealed class PostgreSqlAuditDbContext(DbContextOptions<PostgreSqlAuditDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.PostgreSql);
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

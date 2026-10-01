using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL integration tests for
/// <see cref="EfCoreHealthSnapshotSource{TDbContext}"/> with
/// <see cref="PostgreSqlDatabaseProbeFailureClassifier"/>: the five fixed snapshot outcomes
/// (healthy, database down, dropped table, dropped column, revoked SELECT) plus unclassified
/// propagation and caller cancellation. Enabled via RUN_SERVICEMANTLE_POSTGRES_TESTS=true and
/// Docker availability.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class EfCoreHealthSnapshotSourcePostgreSqlTests : IAsyncLifetime
{
    private const string Prefix = "pghealth";

    private PostgreSqlContainer? container;
    private NpgsqlConnectionStringBuilder? server;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithUsername("probe-admin")
            .WithPassword("probe-password")
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        server = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Healthy_schema_reports_completed_succeeded_reachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await EnsureSchemaAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var source = CreateSource(context);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public async Task Stopped_database_reports_unreachable_with_migration_untouched()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await EnsureSchemaAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var source = CreateSource(context);

        await container!.StopAsync(TestContext.Current.CancellationToken);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Unreachable, snapshot.DatabaseStatus);
        Assert.Equal("pghealth.database_unreachable", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Dropped_table_reports_failed_migration_while_reachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await EnsureSchemaAsync(connectionString);
        await ExecuteInDatabaseAsync(database, "DROP TABLE probe_samples");
        await using var context = CreateContext(connectionString);
        var source = CreateSource(context);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Failed, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Equal("pghealth.schema_unavailable", snapshot.ErrorCode);
        Assert.DoesNotContain("42P01", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("probe_samples", snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropped_column_reports_failed_migration_while_reachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await EnsureSchemaAsync(connectionString);
        await ExecuteInDatabaseAsync(database, "ALTER TABLE probe_samples DROP COLUMN \"Name\"");
        await using var context = CreateContext(connectionString);
        var source = CreateSource(context);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Failed, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Equal("pghealth.schema_unavailable", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Revoked_select_reports_failed_migration_while_reachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await EnsureSchemaAsync(connectionString);

        const string limitedRole = "probe_limited";
        await ExecuteInDatabaseAsync(
            database,
            $"CREATE ROLE {limitedRole} LOGIN PASSWORD 'limited-password'",
            $"GRANT CONNECT ON DATABASE \"{database}\" TO {limitedRole}",
            "GRANT USAGE ON SCHEMA public TO probe_limited",
            "GRANT SELECT ON TABLE probe_samples TO probe_limited",
            "REVOKE SELECT ON TABLE probe_samples FROM probe_limited");

        // The limited role can connect (a reachability fact) but cannot read the mapped table
        // (a schema fact): the snapshot must separate the two.
        var limitedConnection = TargetConnectionString(database, username: limitedRole, password: "limited-password");
        await using var context = CreateContext(limitedConnection);
        var source = CreateSource(context);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Failed, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Equal("pghealth.schema_unavailable", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Unclassified_authentication_failure_propagates_unchanged()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var wrongPasswordConnection = TargetConnectionString(database, password: "wrong-password");
        await using var context = CreateContext(wrongPasswordConnection);
        var source = CreateSource(context);

        // SQLSTATE class 28 is outside the fixed connection-class list: the exception must
        // reach the caller instead of being turned into an invented snapshot state.
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => source.GetSnapshotAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.StartsWith("28", exception.SqlState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_on_the_received_token()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var database = await CreateDatabaseAsync();
        var connectionString = TargetConnectionString(database);
        await using var context = CreateContext(connectionString);
        var source = CreateSource(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(cancelled.Token).AsTask());

        Assert.Equal(cancelled.Token, exception.CancellationToken);
    }

    private EfCoreHealthSnapshotSource<SnapshotDbContext> CreateSource(SnapshotDbContext context)
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        Assert.True(receipt.TryCompleteSucceeded());
        return new EfCoreHealthSnapshotSource<SnapshotDbContext>(
            receipt,
            context,
            new PostgreSqlDatabaseProbeFailureClassifier(),
            Prefix);
    }

    private static SnapshotDbContext CreateContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<SnapshotDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private async Task<string> CreateDatabaseAsync()
    {
        var database = $"health_probe_{UniqueSuffix()}";
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private async Task EnsureSchemaAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    private async Task ExecuteInDatabaseAsync(string database, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(TargetConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private string MaintenanceConnectionString() => TargetConnectionString("postgres");

    private string TargetConnectionString(
        string database,
        string? username = null,
        string? password = null) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = server!.Host,
            Port = server.Port,
            Database = database,
            Username = username ?? server.Username,
            Password = password ?? server.Password!,
            Pooling = false,
        }.ConnectionString;

    private static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    private bool CanRun => ShouldRunPostgreSqlTests() && server is not null;

    private const string SkipReason = "PostgreSQL tests disabled or container not initialized.";

    private static bool ShouldRunPostgreSqlTests()
    {
        var envVar = Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS");
        return envVar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static string GetPostgresImage()
    {
        var envVar = Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE");
        return envVar ?? "postgres:15-alpine";
    }

    private sealed class SnapshotDbContext(DbContextOptions<SnapshotDbContext> options)
        : DbContext(options)
    {
        public DbSet<ProbeSample> Samples => Set<ProbeSample>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeSample>().ToTable("probe_samples");
        }
    }

    private sealed class ProbeSample
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

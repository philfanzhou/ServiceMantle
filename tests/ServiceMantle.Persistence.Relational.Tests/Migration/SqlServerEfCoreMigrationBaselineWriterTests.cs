using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.Relational.Migration;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Persistence.Relational.Tests.Migration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerBaselineWriterCollection
    : ICollectionFixture<SqlServerBaselineWriterFixture>
{
    public const string Name = "SQL Server Baseline Writer";
}

/// <summary>
/// Real SQL Server coverage for <see cref="EfCoreMigrationBaselineWriter"/> on the provider the
/// acceptance pins: the provider's own create-if-not-exists DDL (the OBJECT_ID guard), bracket
/// identifier quoting, T-SQL parameter placeholders, the default and a configured schema-qualified
/// history table, and idempotent re-stamping. Enable with RUN_SERVICEMANTLE_SQLSERVER_TESTS=true
/// (CI); the local arm64 environment has no compatible container and waits for CI.
/// </summary>
[Collection(SqlServerBaselineWriterCollection.Name)]
[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerEfCoreMigrationBaselineWriterTests(SqlServerBaselineWriterFixture fixture)
{
    [Fact]
    public async Task First_write_creates_the_history_table_and_stamps_the_row()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.SqlServer, fixture.ConnectionString is not null);
        var migrationId = $"20260101000000_First_{Guid.NewGuid():N}";
        await using var context = fixture.CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqlConnection(fixture.ConnectionString);

        var inserted = await writer.WriteBaselineAsync(
            connection, migrationId, "10.0.11", TestContext.Current.CancellationToken);

        Assert.True(inserted);
        Assert.Equal(
            (migrationId, "10.0.11"),
            await fixture.ReadRowAsync("[dbo].[__EFMigrationsHistory]", migrationId));
    }

    [Fact]
    public async Task Repeated_write_of_the_same_id_is_idempotent_and_does_not_overwrite()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.SqlServer, fixture.ConnectionString is not null);
        var migrationId = $"20260102000000_Second_{Guid.NewGuid():N}";
        await using var context = fixture.CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqlConnection(fixture.ConnectionString);
        Assert.True(await writer.WriteBaselineAsync(
            connection, migrationId, "10.0.11", TestContext.Current.CancellationToken));

        var reStamped = await writer.WriteBaselineAsync(
            connection, migrationId, "9.9.9-overwrite-attempt", TestContext.Current.CancellationToken);

        Assert.False(reStamped);
        Assert.Equal(
            (migrationId, "10.0.11"),
            await fixture.ReadRowAsync("[dbo].[__EFMigrationsHistory]", migrationId));
    }

    [Fact]
    public async Task Configured_schema_qualified_history_table_is_respected()
    {
        await fixture.EnsureBaselineSchemaAsync();
        var migrationId = $"20260103000000_Third_{Guid.NewGuid():N}";
        await using var context = fixture.CreateCustomSchemaContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqlConnection(fixture.ConnectionString);

        var inserted = await writer.WriteBaselineAsync(
            connection, migrationId, "10.0.11", TestContext.Current.CancellationToken);

        Assert.True(inserted);
        Assert.Equal(
            (migrationId, "10.0.11"),
            await fixture.ReadRowAsync("[baseline].[baseline_history]", migrationId));
    }

    [Fact]
    public async Task Entry_cancellation_throws_operation_canceled_without_writing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var context = fixture.CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqlConnection(fixture.ConnectionString);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.WriteBaselineAsync(
                connection, "20260104000000_Cancelled", "10.0.11", cancellation.Token));

        Assert.Null(await fixture.TryFindRowAsync(
            "[dbo].[__EFMigrationsHistory]", "20260104000000_Cancelled"));
    }
}

public sealed class SqlServerBaselineWriterFixture : IAsyncLifetime
{
    internal const string DatabaseName = "servicemantle_baseline_writer";
    private MsSqlContainer? container;

    internal string? ConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.SqlServer))
        {
            return;
        }

        var image = Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLSERVER_IMAGE")
            ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
        container = new MsSqlBuilder(image).Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var masterConnectionString = container.GetConnectionString();
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        ConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = DatabaseName,
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

    internal DefaultHistoryContext CreateDefaultContext() =>
        new(new DbContextOptionsBuilder<DefaultHistoryContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    internal CustomHistoryContext CreateCustomSchemaContext() =>
        new(new DbContextOptionsBuilder<CustomHistoryContext>()
            .UseSqlServer(
                ConnectionString!,
                sqlServer => sqlServer.MigrationsHistoryTable("baseline_history", "baseline"))
            .Options);

    internal async Task<(string MigrationId, string ProductVersion)?> ReadRowAsync(
        string table,
        string migrationId)
    {
        var row = await TryFindRowAsync(table, migrationId);
        Assert.NotNull(row);
        return row;
    }

    internal async Task<(string MigrationId, string ProductVersion)?> TryFindRowAsync(
        string table,
        string migrationId)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.SqlServer,
            ConnectionString is not null);
        await using var connection = new SqlConnection(ConnectionString!);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MigrationId, ProductVersion FROM {table} WHERE MigrationId = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = migrationId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    internal async Task EnsureBaselineSchemaAsync()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.SqlServer,
            ConnectionString is not null);
        await using var connection = new SqlConnection(ConnectionString!);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'baseline') EXEC('CREATE SCHEMA baseline')";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal sealed class DefaultHistoryContext(DbContextOptions<DefaultHistoryContext> options)
        : DbContext(options)
    {
    }

    internal sealed class CustomHistoryContext(DbContextOptions<CustomHistoryContext> options)
        : DbContext(options)
    {
    }
}

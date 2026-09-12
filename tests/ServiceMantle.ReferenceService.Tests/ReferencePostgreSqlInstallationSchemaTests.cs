using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the ServiceMantle installation table the sample's PostgreSQL context maps through the
/// public EF Core persistence package: the physical schema its dedicated migration produces, the
/// caller-owned transaction boundary around staged installation and workspace rows, and the
/// version concurrency token's behaviour across two independent contexts.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here installs the service. Staging an installation entity is not an installation that
/// is <c>Completed</c>, there is no Setup Code, and no <c>CreatePendingAsync</c> is involved: the
/// rows are staged and saved by this test's own contexts and transactions only.
/// </para>
/// <para>
/// The concurrency case is deliberately narrow: two contexts hold the same explicitly staged row
/// with the same version and both update with the next version. The second save loses the EF
/// optimistic-concurrency race. That is a row-version conflict, not a claim that one instance won
/// a two-instance Setup - nothing here promises a single Setup winner.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlInstallationSchemaTests : IAsyncLifetime
{
    private const string InstallationTable = "service_installations";

    // Synthetic fixture secrets. They exist only inside this container.
    private const string SyntheticUser = "reference_installation_owner";
    private const string SyntheticPassword = "synthetic-reference-installation-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_reference_installation_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        maintenanceConnectionString = container.GetConnectionString();
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
    public async Task The_installation_table_matches_the_runtime_model()
    {
        var target = await CreateMigratedTargetAsync("model_match");
        await using var context = CreateContext(target);

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Token));

        var columns = await ReadInstallationColumnsAsync(target);

        Assert.Equal(
            new Dictionary<string, (string Type, string Nullable, string? Default, int? MaxLength)>
            {
                ["service_id"] = ("character varying", "NO", null, 128),
                ["status"] = ("integer", "NO", null, null),
                ["created_at_utc"] = ("timestamp with time zone", "NO", null, null),
                ["completed_at_utc"] = ("timestamp with time zone", "YES", null, null),
                ["version"] = ("integer", "NO", null, null),
                ["setup_code_generation"] = ("integer", "NO", "0", null),
                ["setup_code_digest"] = ("character varying", "YES", null, 74),
                ["setup_code_issued_at_utc"] = ("timestamp with time zone", "YES", null, null),
                ["setup_code_expires_at_utc"] = ("timestamp with time zone", "YES", null, null),
            },
            columns);
        Assert.Equal(["service_id"], await ReadPrimaryKeyColumnsAsync(target));
        // The migration created the table but initialised no installation row.
        Assert.Empty(await ReadInstallationServiceIdsAsync(target));
    }

    [Fact]
    public async Task Staged_installation_and_workspace_rows_follow_the_caller_s_transaction()
    {
        var target = await CreateMigratedTargetAsync("transaction");

        await using (var context = CreateContext(target))
        {
            await using var rollback = await context.Database.BeginTransactionAsync(Token);
            context.ServiceInstallations.Add(NewInstallation("rollback-service"));
            context.Workspaces.Add(new ReferenceWorkspace
            {
                Id = Guid.NewGuid(),
                DisplayName = "rolled-back",
            });
            await context.SaveChangesAsync(Token);
            await rollback.RollbackAsync(Token);
        }

        Assert.Empty(await ReadInstallationServiceIdsAsync(target));
        Assert.Empty(await ReadWorkspaceNamesAsync(target));

        await using (var context = CreateContext(target))
        {
            await using var commit = await context.Database.BeginTransactionAsync(Token);
            context.ServiceInstallations.Add(NewInstallation("committed-service"));
            context.Workspaces.Add(new ReferenceWorkspace
            {
                Id = Guid.NewGuid(),
                DisplayName = "committed",
            });
            await context.SaveChangesAsync(Token);
            await commit.CommitAsync(Token);
        }

        Assert.Equal(["committed-service"], await ReadInstallationServiceIdsAsync(target));
        Assert.Equal(["committed"], await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task The_second_save_with_a_stale_version_is_an_EF_concurrency_conflict()
    {
        var target = await CreateMigratedTargetAsync("version_conflict");

        await using (var seed = CreateContext(target))
        {
            await using var commit = await seed.Database.BeginTransactionAsync(Token);
            seed.ServiceInstallations.Add(NewInstallation("conflicted-service"));
            await seed.SaveChangesAsync(Token);
            await commit.CommitAsync(Token);
        }

        await using var first = CreateContext(target);
        await using var second = CreateContext(target);
        var firstRow = await first.ServiceInstallations.SingleAsync(Token);
        var secondRow = await second.ServiceInstallations.SingleAsync(Token);

        firstRow.CompletedAtUtc = DateTime.UtcNow;
        firstRow.Version++;
        await first.SaveChangesAsync(Token);

        secondRow.CompletedAtUtc = DateTime.UtcNow;
        secondRow.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(Token));

        Assert.Equal(["conflicted-service"], await ReadInstallationServiceIdsAsync(target));
    }

    private static ServiceInstallationEntity NewInstallation(string serviceId) => new()
    {
        ServiceId = serviceId,
        Status = InstallationStatus.PendingSetup,
        CreatedAtUtc = DateTime.UtcNow,
        Version = 0,
        SetupCodeGeneration = 0,
    };

    private ReferencePostgreSqlDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private async Task<string> CreateMigratedTargetAsync(string name)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            maintenanceConnectionString is not null);
        var database = $"reference_installation_{name}";
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
        var target = new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            IncludeErrorDetail = false,
        }.ConnectionString;
        await using var context = CreateContext(target);
        await context.Database.MigrateAsync(Token);
        return target;
    }

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<List<string>> ReadStringsAsync(string connectionString, string query)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private Task<List<string>> ReadInstallationServiceIdsAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT service_id FROM public."{InstallationTable}" ORDER BY service_id """);

    private Task<List<string>> ReadWorkspaceNamesAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            """SELECT "DisplayName" FROM public."reference_workspaces" ORDER BY "DisplayName" """);

    private Task<List<string>> ReadPrimaryKeyColumnsAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            """
            SELECT key_columns.column_name
            FROM information_schema.table_constraints AS constraints
            JOIN information_schema.key_column_usage AS key_columns
              ON constraints.constraint_name = key_columns.constraint_name
             AND constraints.table_schema = key_columns.table_schema
            WHERE constraints.constraint_type = 'PRIMARY KEY'
              AND constraints.table_schema = 'public'
              AND constraints.table_name = 'service_installations'
            ORDER BY key_columns.ordinal_position
            """);

    private async Task<Dictionary<string, (string Type, string Nullable, string? Default, int? MaxLength)>>
        ReadInstallationColumnsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type, is_nullable, column_default, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'service_installations'
            ORDER BY ordinal_position
            """;
        var columns =
            new Dictionary<string, (string, string, string?, int?)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns[reader.GetString(0)] = (
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4));
        }

        return columns;
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}

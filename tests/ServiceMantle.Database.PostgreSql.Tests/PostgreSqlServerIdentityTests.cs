using System.Data.Common;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlServerIdentityTests(PostgreSqlIdentityFixture fixture)
    : ServerIdentityPreparationTests, IClassFixture<PostgreSqlIdentityFixture>
{
    protected override RealDatabaseProvider DatabaseProvider => RealDatabaseProvider.PostgreSql;
    protected override IDatabaseTargetPreparationProvider Provider => new PostgreSqlDatabaseTargetPreparationProvider();
    protected override string? First => fixture.First;
    protected override string? Second => fixture.Second;
    protected override string DatabaseExistsSql => "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
    protected override string SessionCountSql => "SELECT COUNT(*) FROM pg_stat_activity WHERE backend_type = 'client backend' AND usename = current_user AND pid <> pg_backend_pid()";
    protected override DbConnection CreateConnection(string connection) => new NpgsqlConnection(connection);

    [Fact]
    public async Task Hidden_proof_metadata_fails_closed_and_releases_administrative_locks()
    {
        RequireEnvironment();
        var maintenance = NewName();
        var role = NewName();
        var targetName = NewName();
        await ExecuteAsync(First!, $"CREATE DATABASE {maintenance}");
        await ExecuteAsync(First!, $"CREATE ROLE {role} LOGIN PASSWORD 'identity-password'");
        var administrative = Configure(First!, maintenance);
        await ExecuteAsync(administrative, "REVOKE SELECT ON pg_catalog.pg_locks FROM PUBLIC");
        var target = new NpgsqlConnectionStringBuilder(Configure(First!, targetName))
        {
            Username = role,
            Password = "identity-password"
        }.ConnectionString;

        var result = await PrepareAsync(target, administrative);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.False(await ExistsAsync(First!, targetName));
        await AssertNoSessionsAsync();
        await using var connection = CreateConnection(administrative);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory'";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    protected override string Configure(string connection, string database, bool wrongPassword = false, bool alias = false, int? port = null)
    {
        var value = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Pooling = false,
            Enlist = false
        };
        if (wrongPassword) value.Password = "wrong-identity-password";
        if (alias) value.Host = value.Host == "localhost" ? "127.0.0.1" : "localhost";
        if (port is not null) { value.Host = "127.0.0.1"; value.Port = port.Value; value.SslMode = SslMode.Disable; }
        return value.ConnectionString;
    }
}

public sealed class PostgreSqlIdentityFixture : IAsyncLifetime
{
    private readonly List<PostgreSqlContainer> containers = [];
    public string? First { get; private set; }
    public string? Second { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql)) return;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var instance = new PostgreSqlBuilder("postgres:15-alpine")
                    .WithUsername("identity_admin").WithPassword("identity-password")
                    .Build();
                containers.Add(instance);
                await instance.StartAsync(TestContext.Current.CancellationToken);
                var connection = instance.GetConnectionString();
                var isolated = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false, Enlist = false }.ConnectionString;
                if (i == 0) First = isolated;
                else Second = isolated;
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in containers) await instance.DisposeAsync();
        containers.Clear();
    }
}

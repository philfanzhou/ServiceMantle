using System.Data.Common;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Testing;
using Testcontainers.MySql;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

[RealDatabaseTest(RealDatabaseProvider.MySql)]
public sealed class MySqlServerIdentityTests(MySqlIdentityFixture fixture)
    : ServerIdentityPreparationTests, IClassFixture<MySqlIdentityFixture>
{
    protected override RealDatabaseProvider DatabaseProvider => RealDatabaseProvider.MySql;
    protected override IDatabaseTargetPreparationProvider Provider => new MySqlDatabaseTargetPreparationProvider();
    protected override string? First => fixture.First;
    protected override string? Second => fixture.Second;
    protected override string DatabaseExistsSql => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @name";
    protected override string SessionCountSql => "SELECT COUNT(*) FROM information_schema.processlist WHERE USER = 'root' AND ID <> CONNECTION_ID()";
    protected override DbConnection CreateConnection(string connection) => new MySqlConnection(connection);

    [Fact]
    public async Task Target_without_database_metadata_privileges_can_prove_the_server_using_public_lock_state()
    {
        RequireEnvironment();
        var role = NewName();
        var targetName = NewName();
        await ExecuteAsync(First!, $"CREATE USER '{role}'@'%' IDENTIFIED BY 'identity-password'");
        var target = new MySqlConnectionStringBuilder(Configure(First!, targetName))
        {
            UserID = role,
            Password = "identity-password"
        }.ConnectionString;

        var result = await PrepareAsync(target, First!);

        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.True(await ExistsAsync(First!, targetName));
        // The target login has no database grants: creation did not grant or repair access.
        Assert.False(await ExistsAsync(Configure(target, ""), targetName));
        await AssertNoSessionsAsync();
    }

    protected override string Configure(string connection, string database, bool wrongPassword = false, bool alias = false, int? port = null)
    {
        var value = new MySqlConnectionStringBuilder(connection)
        {
            Database = database,
            Pooling = false,
            AutoEnlist = false
        };
        if (wrongPassword) value.Password = "wrong-identity-password";
        if (alias) value.Server = value.Server == "localhost" ? "127.0.0.1" : "localhost";
        if (port is not null) { value.Server = "127.0.0.1"; value.Port = (uint)port.Value; value.SslMode = MySqlSslMode.None; }
        return value.ConnectionString;
    }
}

public sealed class MySqlIdentityFixture : IAsyncLifetime
{
    private readonly List<MySqlContainer> containers = [];
    public string? First { get; private set; }
    public string? Second { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.MySql)) return;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var instance = new MySqlBuilder("mysql:8.4")
                    .WithUsername("identity_admin").WithPassword("identity-password")
                    .Build();
                containers.Add(instance);
                await instance.StartAsync(TestContext.Current.CancellationToken);
                var connection = instance.GetConnectionString();
                var isolated = new MySqlConnectionStringBuilder(connection) { UserID = "root", Database = "", Pooling = false, AutoEnlist = false }.ConnectionString;
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

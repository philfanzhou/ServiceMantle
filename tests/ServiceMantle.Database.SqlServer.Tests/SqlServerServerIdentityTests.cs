using System.Data.Common;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests;

[Collection("SQL Server identity")]
[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerServerIdentityTests(SqlServerIdentityFixture fixture)
    : ServerIdentityPreparationTests, IClassFixture<SqlServerIdentityFixture>
{
    protected override RealDatabaseProvider DatabaseProvider => RealDatabaseProvider.SqlServer;
    protected override IDatabaseTargetPreparationProvider Provider => new SqlServerDatabaseTargetPreparationProvider();
    protected override string? First => fixture.First;
    protected override string? Second => fixture.Second;
    protected override string DatabaseExistsSql => "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
    protected override string SessionCountSql => "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND session_id <> @@SPID";
    protected override DbConnection CreateConnection(string connection) => new SqlConnection(connection);

    [Fact]
    public async Task Target_without_master_access_cannot_prove_the_server_or_create()
    {
        RequireEnvironment();
        var role = NewName();
        var name = NewName();
        await ExecuteAsync(First!, $"CREATE LOGIN [{role}] WITH PASSWORD = 'Identity-password-1', CHECK_POLICY = OFF");
        await ExecuteAsync(First!, $"CREATE USER [{role}] FOR LOGIN [{role}]; DENY CONNECT TO [{role}]");
        var target = new SqlConnectionStringBuilder(Configure(First!, name))
        {
            UserID = role,
            Password = "Identity-password-1"
        }.ConnectionString;

        var result = await PrepareAsync(target, First!);

        Assert.False(result.Succeeded);
        Assert.Contains(result.ErrorCode, new[]
        {
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed
        });
        Assert.False(await ExistsAsync(First!, name));
        await AssertNoSessionsAsync();
    }

    protected override string Configure(string connection, string database, bool wrongPassword = false, bool alias = false, int? port = null)
    {
        var value = new SqlConnectionStringBuilder(connection)
        {
            InitialCatalog = string.IsNullOrEmpty(database) ? "master" : database,
            Pooling = false,
            Enlist = false,
            ConnectRetryCount = 0
        };
        if (wrongPassword) value.Password = "wrong-identity-password";
        if (alias) value.DataSource = value.DataSource.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        if (port is not null) { value.DataSource = "127.0.0.1," + port; value.Encrypt = SqlConnectionEncryptOption.Optional; }
        return value.ConnectionString;
    }
}

public sealed class SqlServerIdentityFixture : IAsyncLifetime
{
    private readonly List<MsSqlContainer> containers = [];
    public string? First { get; private set; }
    public string? Second { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.SqlServer)) return;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var instance = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
                    .WithPassword("Identity-password-1").WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
                    .Build();
                containers.Add(instance);
                await instance.StartAsync(TestContext.Current.CancellationToken);
                var connection = instance.GetConnectionString();
                var isolated = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master", Pooling = false, Enlist = false }.ConnectionString;
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

// Two independent SQL Server processes already exercise this contract concurrently.
// Do not overlap their fixture lifetime with the migration-lock container on CI runners.
[CollectionDefinition("SQL Server identity", DisableParallelization = true)]
public sealed class SqlServerIdentityCollection;

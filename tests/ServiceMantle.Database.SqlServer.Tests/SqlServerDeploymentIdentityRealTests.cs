using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests;

[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
[Collection("SQL Server deployment identity")]
public sealed class SqlServerDeploymentIdentityRealTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_catalog_collation_controls_canonical_name_and_owned_session_release(bool caseSensitive)
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.SqlServer))
        { RealDatabaseTestEnvironment.RequireAvailable(RealDatabaseProvider.SqlServer, false); return; }
        var expectedCollation = caseSensitive ? "SQL_Latin1_General_CP1_CS_AS" : "SQL_Latin1_General_CP1_CI_AS";
        // Initialize the system catalog before accepting clients. The pinned image does not
        // apply MSSQL_COLLATION to the packaged system database templates on ordinary startup.
        // This is the same --setup/-q path used by its mssql-conf/set-collation.sh.
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Deployment-password-1").WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
            .WithEntrypoint("/bin/bash", "-c")
            .WithCommand($"/opt/mssql/bin/sqlservr --setup -q{expectedCollation} && exec /opt/mssql/bin/sqlservr").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var administrator = new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = "master", Pooling = false, Enlist = false };
        await using (var setup = new SqlConnection(administrator.ConnectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('Collation'))";
            var collation = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(expectedCollation, collation);
            command.CommandText = "CREATE DATABASE [CaseDb];";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            // USE is compiled only after the database exists, in a separate server batch.
            command.CommandText = "CREATE LOGIN [identity_user] WITH PASSWORD = 'Identity-password-1'; USE [CaseDb]; CREATE USER [identity_user] FOR LOGIN [identity_user];";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var builder = new SqlConnectionStringBuilder(administrator.ConnectionString) { InitialCatalog = "CaseDb", UserID = "identity_user", Password = "Identity-password-1" };
        var provider = new SqlServerDatabaseDeploymentCapabilityProvider();
        BootstrapDatabaseConfiguration Target() => new(WellKnownDatabaseProviderIds.SqlServer, "16", builder.ConnectionString);
        var first = await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
        Assert.NotEmpty(first);
        await using (var verifier = new SqlConnection(administrator.ConnectionString))
        {
            await verifier.OpenAsync(TestContext.Current.CancellationToken);
            await using var sessions = verifier.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND login_name = 'identity_user'";
            Assert.Equal(0, Convert.ToInt32(await sessions.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        builder.UserID = administrator.UserID; builder.Password = administrator.Password;
        Assert.Equal(first, await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        builder.InitialCatalog = "casedb";
        if (caseSensitive)
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        else
            Assert.Equal(first, await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        await using (var setup = new SqlConnection(administrator.ConnectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = caseSensitive ? "CREATE DATABASE [Distinct]; CREATE DATABASE [distinct];" : "CREATE DATABASE [Distinct];";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        builder.InitialCatalog = "Distinct";
        var distinct = await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
        builder.InitialCatalog = "distinct";
        Assert.Equal(!caseSensitive, distinct == await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        builder.Password = "invalid-secret";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(error.InnerException); Assert.DoesNotContain("invalid-secret", error.ToString());
    }
}

[CollectionDefinition("SQL Server deployment identity", DisableParallelization = true)]
public sealed class SqlServerDeploymentIdentityCollection;

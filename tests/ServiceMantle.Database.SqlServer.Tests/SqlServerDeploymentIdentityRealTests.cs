using DotNet.Testcontainers.Builders;
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
        const string setupComplete = "/tmp/servicemantle-catalog-setup-complete";
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Deployment-password-1").WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
            .WithEntrypoint("/bin/bash", "-c")
            .WithCommand($"/opt/mssql/bin/sqlservr --setup -q{expectedCollation} && touch {setupComplete} && exec /opt/mssql/bin/sqlservr")
            // --setup itself accepts SQL queries before exiting. A successful SELECT 1
            // alone can therefore signal readiness of the process that is about to stop.
            // Wait for setup to exit, then query the final server before creating clients.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilFileExists(setupComplete)
                .UntilCommandIsCompleted("/opt/mssql-tools18/bin/sqlcmd", "-C", "-b", "-r", "1", "-d", "master", "-Q", "SELECT 1;"))
            .Build();
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
        var probe = new DiagnosticProbe();
        var provider = new SqlServerDatabaseDeploymentCapabilityProvider(probe);
        BootstrapDatabaseConfiguration Target() => new(WellKnownDatabaseProviderIds.SqlServer, "16", builder.ConnectionString);
        async Task<string> ReadIdentityAsync()
        {
            try
            {
                return await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
            }
            catch (InvalidOperationException) when (probe.FailureDiagnostics is not null)
            {
                // Diagnose the fixture without exposing the driver's messages, connection
                // string, or credentials, or changing the provider's safe exception contract.
                throw new Xunit.Sdk.XunitException(probe.FailureDiagnostics);
            }
        }
        var first = await ReadIdentityAsync();
        Assert.NotEmpty(first);
        await using (var verifier = new SqlConnection(administrator.ConnectionString))
        {
            await verifier.OpenAsync(TestContext.Current.CancellationToken);
            await using var sessions = verifier.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND login_name = 'identity_user'";
            Assert.Equal(0, Convert.ToInt32(await sessions.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        builder.UserID = administrator.UserID; builder.Password = administrator.Password;
        Assert.Equal(first, await ReadIdentityAsync());
        builder.InitialCatalog = "casedb";
        if (caseSensitive)
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        else
            Assert.Equal(first, await ReadIdentityAsync());
        await using (var setup = new SqlConnection(administrator.ConnectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            // Case-sensitive catalog names do not give the default physical files distinct
            // paths. Keep both logical names while assigning unrelated MDF/LDF filenames.
            command.CommandText = """
                CREATE DATABASE [Distinct]
                ON PRIMARY (NAME = N'deployment_upper_data', FILENAME = N'/var/opt/mssql/data/deployment_upper.mdf')
                LOG ON (NAME = N'deployment_upper_log', FILENAME = N'/var/opt/mssql/data/deployment_upper.ldf');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            if (caseSensitive)
            {
                command.CommandText = """
                    CREATE DATABASE [distinct]
                    ON PRIMARY (NAME = N'deployment_lower_data', FILENAME = N'/var/opt/mssql/data/deployment_lower.mdf')
                    LOG ON (NAME = N'deployment_lower_log', FILENAME = N'/var/opt/mssql/data/deployment_lower.ldf');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }
        builder.InitialCatalog = "Distinct";
        var distinct = await ReadIdentityAsync();
        builder.InitialCatalog = "distinct";
        Assert.Equal(!caseSensitive, distinct == await ReadIdentityAsync());
        builder.Password = "invalid-secret";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(error.InnerException); Assert.DoesNotContain("invalid-secret", error.ToString());
    }

    private sealed class DiagnosticProbe : ISqlServerCanonicalTargetProbe
    {
        private readonly SqlServerCanonicalTargetProbe inner = new();
        public string? FailureDiagnostics { get; private set; }

        public async ValueTask<string?> ReadAsync(SqlConnectionStringBuilder builder, CancellationToken token)
        {
            FailureDiagnostics = null;
            try
            {
                return await inner.ReadAsync(builder, token);
            }
            catch (SqlException exception)
            {
                FailureDiagnostics = "SQL Server identity probe failed; " + string.Join(", ",
                    exception.Errors.Cast<SqlError>().Select(error =>
                        $"number={error.Number}, state={error.State}, class={error.Class}"));
                throw;
            }
        }
    }
}

[CollectionDefinition("SQL Server deployment identity", DisableParallelization = true)]
public sealed class SqlServerDeploymentIdentityCollection;

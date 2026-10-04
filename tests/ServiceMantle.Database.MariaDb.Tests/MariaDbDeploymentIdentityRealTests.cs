using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Testing;
using Testcontainers.MariaDb;
using Xunit;

namespace ServiceMantle.Database.MariaDb.Tests;

[RealDatabaseTest(RealDatabaseProvider.MariaDb)]
public sealed class MariaDbDeploymentIdentityRealTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Canonical_identity_uses_the_real_server_case_rule_and_owned_connection(int rule)
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.MariaDb))
        { RealDatabaseTestEnvironment.RequireAvailable(RealDatabaseProvider.MariaDb, false); return; }
        await using var container = new MariaDbBuilder("mariadb:11.4").WithDatabase("CaseDb")
            .WithUsername("identity_user").WithPassword("identity-password")
            .WithCommand($"--lower-case-table-names={rule}").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var builder = new MySqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false, AutoEnlist = false };
        var provider = new MariaDbDatabaseDeploymentCapabilityProvider();
        BootstrapDatabaseConfiguration Target() => new(WellKnownDatabaseProviderIds.MariaDb, "11.4", builder.ConnectionString);
        var first = await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
        Assert.NotEmpty(first);
        var administrator = new MySqlConnectionStringBuilder(builder.ConnectionString) { UserID = "root" };
        await using (var verifier = new MySqlConnection(administrator.ConnectionString))
        {
            await verifier.OpenAsync(TestContext.Current.CancellationToken);
            await using var sessions = verifier.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM information_schema.processlist WHERE USER = 'identity_user'";
            Assert.Equal(0L, Convert.ToInt64(await sessions.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        builder.UserID = "root";
        var differentCredential = await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
        Assert.Equal(first, differentCredential);
        builder.Database = "casedb";
        if (rule == 1)
            Assert.Equal(first, await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        builder.Database = "";
        await using (var connection = new MySqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = rule == 0 ? "CREATE DATABASE `Distinct`; CREATE DATABASE `distinct`;" : "CREATE DATABASE `Distinct`;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        builder.Database = "Distinct";
        var distinct = await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken);
        builder.Database = "distinct";
        Assert.Equal(rule == 1, distinct == await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        builder.Password = "invalid-secret";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(error.InnerException); Assert.DoesNotContain("invalid-secret", error.ToString());
        // No cached/pool-owned session is retained: a later valid credential resolves afresh.
        builder.Password = "identity-password";
        Assert.NotEmpty(await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
    }
}

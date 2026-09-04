using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Testing;

// The same behavioral contract runs against two independent real instances of each provider.
public abstract class ServerIdentityPreparationTests
{
    protected abstract RealDatabaseProvider DatabaseProvider { get; }
    protected abstract IDatabaseTargetPreparationProvider Provider { get; }
    protected abstract string? First { get; }
    protected abstract string? Second { get; }
    protected abstract string Configure(string connection, string database, bool wrongPassword = false, bool alias = false, int? port = null);
    protected abstract DbConnection CreateConnection(string connection);
    protected abstract string DatabaseExistsSql { get; }
    protected abstract string SessionCountSql { get; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_real_instance_is_rejected_even_if_it_already_has_the_name(bool existing)
    {
        RequireEnvironment();
        var name = NewName();
        if (existing)
        {
            var setup = await PrepareAsync(Configure(Second!, name), Second!);
            Assert.Equal(DatabaseTargetPreparationOutcome.Created, setup.Outcome);
        }

        var result = await PrepareAsync(Configure(First!, name), Second!);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.False(await ExistsAsync(First!, name));
        Assert.Equal(existing, await ExistsAsync(Second!, name));
        AssertSafe(result.ToString());
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task Different_connection_aliases_create_a_missing_target_and_repeat_safely()
    {
        RequireEnvironment();
        var name = NewName();
        var target = Configure(First!, name, alias: true);
        var created = await PrepareAsync(target, First!);
        var repeated = await PrepareAsync(target, First!);

        Assert.Equal(DatabaseTargetPreparationOutcome.Created, created.Outcome);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, repeated.Outcome);
        Assert.True(await ExistsAsync(First!, name));
        Assert.False(await ExistsAsync(Second!, name));
        await AssertNoSessionsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unusable_credentials_fail_closed_and_are_not_replaced_by_the_other_identity(bool administrative)
    {
        RequireEnvironment();
        var name = NewName();
        var target = Configure(First!, name, wrongPassword: !administrative);
        var admin = administrative ? Configure(First!, "", wrongPassword: true) : First!;

        var result = await PrepareAsync(target, admin);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed, result.ErrorCode);
        Assert.False(await ExistsAsync(First!, name));
        AssertSafe(result.ToString());
        await AssertNoSessionsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unresponsive_target_proof_preserves_timeout_and_caller_cancellation(bool cancel)
    {
        RequireEnvironment();
        var name = NewName();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var caller = new CancellationTokenSource();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(Provider.ProviderId, "16", Configure(First!, name, port: port)), First!);
        var preparation = Provider.PrepareAsync(
            request, TimeSpan.FromSeconds(cancel ? 15 : 2), caller.Token).AsTask();
        using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (cancel)
        {
            await caller.CancelAsync();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            Assert.Equal(caller.Token, exception.CancellationToken);
            Assert.Null(exception.InnerException);
            AssertSafe(exception.ToString());
        }
        else
        {
            var result = await preparation;
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
            AssertSafe(result.ToString());
        }

        Assert.False(await ExistsAsync(First!, name));
        await AssertNoSessionsAsync();
    }

    protected Task<DatabaseTargetPreparationResult> PrepareAsync(string target, string administrative) =>
        Provider.PrepareAsync(
            new DatabaseTargetPreparationRequest(new BootstrapDatabaseConfiguration(Provider.ProviderId, "16", target), administrative),
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).AsTask();

    protected async Task ExecuteAsync(string connection, string sql)
    {
        await using var opened = CreateConnection(connection);
        await opened.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = opened.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    protected async Task<bool> ExistsAsync(string connection, string name)
    {
        await using var opened = CreateConnection(connection);
        await opened.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = opened.CreateCommand();
        command.CommandText = DatabaseExistsSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) != 0;
    }

    protected void RequireEnvironment() =>
        RealDatabaseTestEnvironment.RequireAvailable(DatabaseProvider, First is not null && Second is not null);

    protected static string NewName() => "identity_" + Guid.NewGuid().ToString("N")[..12];

    private static void AssertSafe(string output)
    {
        foreach (var secret in new[] { "identity-password", "wrong-identity-password", "localhost", "127.0.0.1", "Password=", "User ID=", "Username=" })
        {
            Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    protected async Task AssertNoSessionsAsync()
    {
        foreach (var connection in new[] { First!, Second! })
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var opened = CreateConnection(connection);
                await opened.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = opened.CreateCommand();
                command.CommandText = SessionCountSql;
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                if (count == 0) break;
                Assert.True(attempt < 50, "A preparation session remained open after completion.");
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}

using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleConnectionStringBoundaryTests
{
    private const string ValidConnection =
        "Data Source=private-host/FREEPDB1;User Id=app_user;Password=Target-Secret-1";

    public static TheoryData<string> RejectedConnections => new()
    {
        ValidConnection + ";UnknownAttribute=Private-Value",
        ValidConnection + ";Token Authentication=OAUTH",
        ValidConnection + ";Wallet Location=/private/wallet",
        ValidConnection + ";DBA Privilege=SYSDBA",
        ValidConnection + ";Proxy User Id=Private-Proxy",
        ValidConnection + ";Proxy Password=Private-Password",
        ValidConnection + ";Connection Timeout=not-a-number",
        ValidConnection + ";Pooling=not-a-boolean",
        ValidConnection + ";MalformedSegment",
        ValidConnection + ";Password=\"unterminated"
    };

    [Theory]
    [MemberData(nameof(RejectedConnections))]
    public async Task Every_input_boundary_rejects_parse_or_identity_failures_before_IO(string connection)
    {
        var operations = new FakeOracleOperations();
        var bootstrap = new OracleBootstrapDatabaseProvider(operations);
        var preparation = new OracleDatabaseTargetPreparationProvider(operations);
        var token = TestContext.Current.CancellationToken;

        var validation = await bootstrap.ValidateAsync(Target(connection), token);
        var observation = await preparation.ObserveAsync(Target(connection), token);
        var invalidTarget = await preparation.PrepareAsync(
            new(Target(connection), ValidConnection), TimeSpan.FromSeconds(1), token);
        var invalidAdmin = await preparation.PrepareAsync(
            new(Target(ValidConnection), connection), TimeSpan.FromSeconds(1), token);

        Assert.Equal("database.connection_string_invalid", validation.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
        Assert.Equal(DatabaseTargetObservationStatus.ServerUnreachable, observation.Status);
        Assert.Null(observation.TargetExists);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, invalidTarget.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, invalidAdmin.ErrorCode);
        Assert.Equal(0, operations.ProbeCount);
        Assert.Equal(0, operations.OpenCount);
        foreach (var result in new object[] { validation, observation, invalidTarget, invalidAdmin })
        {
            var diagnostics = result + JsonSerializer.Serialize(result);
            foreach (var secret in new[]
                { "private-host", "FREEPDB1", "app_user", "Target-Secret-1", "Private-", "wallet", "ORA-", "OracleException" })
            {
                Assert.DoesNotContain(secret, diagnostics, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [MemberData(nameof(RejectedConnections))]
    public async Task Caller_cancellation_precedes_invalid_input_at_all_boundaries(string connection)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var operations = new FakeOracleOperations();
        var bootstrap = new OracleBootstrapDatabaseProvider(operations);
        var preparation = new OracleDatabaseTargetPreparationProvider(operations);
        Func<Task>[] calls =
        [
            () => bootstrap.ValidateAsync(Target(connection), source.Token).AsTask(),
            () => preparation.ObserveAsync(Target(connection), source.Token).AsTask(),
            () => preparation.PrepareAsync(new(Target(connection), ValidConnection),
                TimeSpan.FromSeconds(1), source.Token).AsTask(),
            () => preparation.PrepareAsync(new(Target(ValidConnection), connection),
                TimeSpan.FromSeconds(1), source.Token).AsTask()
        ];
        foreach (var call in calls)
        {
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(call);
            Assert.Equal(source.Token, exception.CancellationToken);
            Assert.Null(exception.InnerException);
            Assert.DoesNotContain("Target-Secret-1", exception.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(0, operations.ProbeCount);
        Assert.Equal(0, operations.OpenCount);
    }

    private static BootstrapDatabaseConfiguration Target(string connection) =>
        new(WellKnownDatabaseProviderIds.Oracle, "23.26.1.0", connection);
}

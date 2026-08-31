using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleBootstrapDatabaseProviderTests
{
    private const string ConnectionString =
        "Data Source=localhost/FREEPDB1;User Id=app_user;Password=Target-Secret-1";

    [Fact]
    public void Descriptor_fixes_Oracle_server_schema_and_required_version()
    {
        var descriptor = new OracleBootstrapDatabaseProvider().Descriptor;

        Assert.Equal(WellKnownDatabaseProviderIds.Oracle, descriptor.Id);
        Assert.Equal("Oracle", descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerSchema, descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Required, descriptor.ServerVersionRequirement);
        Assert.Empty(descriptor.Aliases);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("19.3")]
    [InlineData("21.11.0")]
    [InlineData("23.26.1.0")]
    public async Task Supported_numeric_versions_are_accepted(string version)
    {
        var result = await CreateProvider(OracleTargetProbeOutcome.Success).ValidateAsync(
            CreateTarget(version),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("19.")]
    [InlineData("19c")]
    [InlineData("23.1.2.3.4.5")]
    public async Task Invalid_versions_fail_before_the_probe(string version)
    {
        var operations = new FakeOracleOperations();
        var result = await new OracleBootstrapDatabaseProvider(operations).ValidateAsync(
            CreateTarget(version),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_invalid", result.ErrorCode);
        Assert.Equal(0, operations.ProbeCount);
    }

    [Theory]
    [InlineData("11.2")]
    [InlineData("18.9")]
    public async Task Versions_before_19_are_rejected(string version)
    {
        var result = await CreateProvider(OracleTargetProbeOutcome.Success).ValidateAsync(
            CreateTarget(version),
            TestContext.Current.CancellationToken);

        Assert.Equal("database.server_version_unsupported", result.ErrorCode);
    }

    public static TheoryData<int, string?> ProbeOutcomes => new()
    {
        { (int)OracleTargetProbeOutcome.Success, null },
        { (int)OracleTargetProbeOutcome.IdentityMismatch, "database.connection_string_invalid" },
        { (int)OracleTargetProbeOutcome.UnsupportedTopology, "database.connection_string_invalid" },
        { (int)OracleTargetProbeOutcome.TopologyPermissionDenied, "database.permission_denied" },
        { (int)OracleTargetProbeOutcome.CreateSessionDenied, "database.permission_denied" },
        { (int)OracleTargetProbeOutcome.AccountLocked, "database.authentication_failed" },
        { (int)OracleTargetProbeOutcome.PasswordExpired, "database.authentication_failed" },
        { (int)OracleTargetProbeOutcome.InvalidCredentials, "database.authentication_failed" },
        { (int)OracleTargetProbeOutcome.ConnectionFailed, "database.connection_failed" },
        { (int)OracleTargetProbeOutcome.ValidationFailed, "database.provider_validation_failed" }
    };

    [Theory]
    [MemberData(nameof(ProbeOutcomes))]
    public async Task Probe_outcomes_map_to_stable_safe_validation_codes(
        int outcomeValue,
        string? errorCode)
    {
        var outcome = (OracleTargetProbeOutcome)outcomeValue;
        var result = await CreateProvider(outcome).ValidateAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(errorCode is null, result.IsValid);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.DoesNotContain("Target-Secret-1", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("FREEPDB1", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=db/FREEPDB1;User Id=C##COMMON;Password=Target-Secret-1")]
    [InlineData("Data Source=db/FREEPDB1;User Id=APP;Password=bad\"password")]
    [InlineData("Data Source=db/FREEPDB1;User Id=APP;Password=Target-Secret-1;DBA Privilege=SYSDBA")]
    [InlineData("Data Source=db/FREEPDB1;User Id=APP;Password=Target-Secret-1;Proxy User Id=PROXY")]
    public async Task Unsupported_identity_shapes_are_rejected_without_connecting(string connectionString)
    {
        var operations = new FakeOracleOperations();
        var result = await new OracleBootstrapDatabaseProvider(operations).ValidateAsync(
            CreateTarget(connectionString: connectionString),
            TestContext.Current.CancellationToken);

        Assert.Equal("database.connection_string_invalid", result.ErrorCode);
        Assert.Equal(0, operations.ProbeCount);
    }

    [Fact]
    public async Task Target_user_is_canonicalized_and_connection_timeout_is_bounded()
    {
        var operations = new FakeOracleOperations();
        var result = await new OracleBootstrapDatabaseProvider(operations).ValidateAsync(
            CreateTarget(connectionString: ConnectionString + ";Connection Timeout=60"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal("APP_USER", operations.LastExpectedUserName);
        Assert.Equal(8, operations.LastTargetConnectionString!.ConnectionTimeout);
    }

    [Fact]
    public async Task Cancellation_replaces_a_probe_exception_with_safe_diagnostics()
    {
        const string secret = "do-not-echo";
        using var source = new CancellationTokenSource();
        var operations = new FakeOracleOperations
        {
            ProbeHandler = (_, _, _) =>
            {
                source.Cancel();
                throw new InvalidOperationException($"Password={secret};Data Source=private");
            }
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OracleBootstrapDatabaseProvider(operations)
                .ValidateAsync(CreateTarget(), source.Token)
                .AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", exception.ToString(), StringComparison.Ordinal);
    }

    private static OracleBootstrapDatabaseProvider CreateProvider(OracleTargetProbeOutcome outcome) =>
        new(new FakeOracleOperations { ProbeOutcome = outcome });

    private static BootstrapDatabaseConfiguration CreateTarget(
        string version = "23.26.1.0",
        string connectionString = ConnectionString) =>
        new(WellKnownDatabaseProviderIds.Oracle, version, connectionString);
}

internal sealed class FakeOracleOperations : IOracleDatabaseOperations
{
    private readonly Queue<IOracleAdministrativeSession> sessions = new();

    internal OracleTargetProbeOutcome ProbeOutcome { get; set; } = OracleTargetProbeOutcome.Success;
    internal Func<OracleConnectionStringBuilder, string, CancellationToken, ValueTask<OracleTargetProbeOutcome>>?
        ProbeHandler
    { get; set; }
    internal int ProbeCount { get; private set; }
    internal int OpenCount { get; private set; }
    internal string? LastExpectedUserName { get; private set; }
    internal OracleConnectionStringBuilder? LastTargetConnectionString { get; private set; }
    internal OracleConnectionStringBuilder? LastAdministrativeConnectionString { get; private set; }

    internal void EnqueueSession(IOracleAdministrativeSession session) => sessions.Enqueue(session);

    public ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        ProbeCount++;
        LastExpectedUserName = expectedUserName;
        LastTargetConnectionString = connectionString;
        return ProbeHandler?.Invoke(connectionString, expectedUserName, cancellationToken) ??
            ValueTask.FromResult(ProbeOutcome);
    }

    public ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
        OracleConnectionStringBuilder connectionString,
        string expectedUserName,
        CancellationToken cancellationToken)
    {
        OpenCount++;
        LastExpectedUserName = expectedUserName;
        LastAdministrativeConnectionString = connectionString;
        if (sessions.Count == 0)
        {
            throw new OracleOperationException(OracleFailureKind.Unexpected);
        }

        return ValueTask.FromResult(sessions.Dequeue());
    }
}

using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

public sealed class MySqlBootstrapDatabaseProviderTests
{
    private const string ValidConnectionString =
        "Server=localhost;Database=app;User ID=app;Password=target-secret";

    [Fact]
    public void Descriptor_is_logically_independent_and_requires_a_server_database_version()
    {
        var descriptor = new MySqlBootstrapDatabaseProvider().Descriptor;

        Assert.Equal(WellKnownDatabaseProviderIds.MySql, descriptor.Id);
        Assert.Equal("MySQL", descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Required, descriptor.ServerVersionRequirement);
        Assert.Empty(descriptor.Aliases);
        Assert.NotEqual(WellKnownDatabaseProviderIds.MariaDb, descriptor.Id);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("8.0")]
    [InlineData("8.0.36")]
    [InlineData("9.1")]
    public async Task ValidateAsync_accepts_supported_numeric_versions(string serverVersion)
    {
        var result = await CreateProvider(MySqlProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.")]
    [InlineData("8.x")]
    [InlineData("8.0.36.1")]
    [InlineData("8.0-commercial")]
    public async Task ValidateAsync_rejects_invalid_versions(string serverVersion)
    {
        var result = await CreateProvider(MySqlProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("5.7")]
    [InlineData("7.9.9")]
    public async Task ValidateAsync_rejects_unsupported_versions(string serverVersion)
    {
        var result = await CreateProvider(MySqlProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_unsupported", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_rejects_MariaDb_provider_id_without_probing()
    {
        var probe = new FakeProbe(MySqlProbeOutcome.Success);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MariaDb,
            "11.4",
            ValidConnectionString);

        var result = await new MySqlBootstrapDatabaseProvider(probe).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_mismatch", result.ErrorCode);
        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    [InlineData("Server==localhost;Database=app")]
    [InlineData("Server=localhost;User ID=app;Password=secret")]
    [InlineData("Server=localhost;Database=bad\nname;User ID=app")]
    public async Task ValidateAsync_rejects_invalid_connection_or_database_name(string connectionString)
    {
        var target = CreateTarget("8.0", connectionString);

        var result = await CreateProvider(MySqlProbeOutcome.Success).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.True(
            result.ErrorCode is "database.connection_string_invalid" or "database.database_required");
    }

    [Fact]
    public async Task ValidateAsync_bounds_connection_and_command_timeouts()
    {
        var probe = new FakeProbe(MySqlProbeOutcome.Success);
        var target = CreateTarget(
            "8.4",
            ValidConnectionString + ";Connection Timeout=60;Default Command Timeout=90");

        var result = await new MySqlBootstrapDatabaseProvider(probe).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(8U, probe.LastConnectionString!.ConnectionTimeout);
        Assert.Equal(5U, probe.LastConnectionString.DefaultCommandTimeout);
        Assert.Equal(5, probe.LastCommandTimeoutSeconds);
    }

    public static TheoryData<int, string?> ProbeOutcomes => new()
    {
        { (int)MySqlProbeOutcome.Success, null },
        { (int)MySqlProbeOutcome.TargetIdentityMismatch, "database.connection_string_invalid" },
        { (int)MySqlProbeOutcome.DatabaseNotFound, "database.target_not_found" },
        { (int)MySqlProbeOutcome.AuthenticationFailed, "database.authentication_failed" },
        { (int)MySqlProbeOutcome.TargetAccessDenied, "database.permission_denied" },
        { (int)MySqlProbeOutcome.ConnectionFailed, "database.connection_failed" },
        { (int)MySqlProbeOutcome.ValidationFailed, "database.provider_validation_failed" },
    };

    [Theory]
    [MemberData(nameof(ProbeOutcomes))]
    public async Task ValidateAsync_maps_probe_outcomes_without_exposing_connection_values(
        int outcomeValue,
        string? errorCode)
    {
        var outcome = (MySqlProbeOutcome)outcomeValue;
        var result = await CreateProvider(outcome).ValidateAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(errorCode is null, result.IsValid);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.DoesNotContain("target-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_cancellation_discards_the_raw_probe_exception()
    {
        const string secret = "bootstrap-cancel-secret";
        using var source = new CancellationTokenSource();
        var probe = new FakeProbe(MySqlProbeOutcome.Success, _ =>
        {
            source.Cancel();
            throw new InvalidOperationException($"Server=internal;User ID=admin;Password={secret}");
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new MySqlBootstrapDatabaseProvider(probe)
                .ValidateAsync(CreateTarget(), source.Token)
                .AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Server=internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<MySqlErrorCode, int> ErrorClassifications => new()
    {
        { MySqlErrorCode.UnknownDatabase, (int)MySqlProbeOutcome.DatabaseNotFound },
        { MySqlErrorCode.AccessDenied, (int)MySqlProbeOutcome.AuthenticationFailed },
        { MySqlErrorCode.DatabaseAccessDenied, (int)MySqlProbeOutcome.TargetAccessDenied },
        { MySqlErrorCode.UnableToConnectToHost, (int)MySqlProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.ConnectionCountError, (int)MySqlProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.CommandTimeoutExpired, (int)MySqlProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.QueryTimeout, (int)MySqlProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.ParseError, (int)MySqlProbeOutcome.ValidationFailed },
    };

    [Theory]
    [MemberData(nameof(ErrorClassifications))]
    public void Failure_classifier_maps_only_stable_error_categories(
        MySqlErrorCode errorCode,
        int expectedValue)
    {
        var expected = (MySqlProbeOutcome)expectedValue;
        Assert.Equal(expected, MySqlProbeFailureClassifier.Classify(errorCode));
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public void Failure_classifier_maps_transport_exceptions_without_using_their_messages(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(MySqlProbeOutcome.ConnectionFailed, MySqlProbeFailureClassifier.Classify(exception));
    }

    private static MySqlBootstrapDatabaseProvider CreateProvider(MySqlProbeOutcome outcome) =>
        new(new FakeProbe(outcome));

    private static BootstrapDatabaseConfiguration CreateTarget(
        string serverVersion = "8.0.36",
        string connectionString = ValidConnectionString) =>
        new(WellKnownDatabaseProviderIds.MySql, serverVersion, connectionString);

    private sealed class FakeProbe(
        MySqlProbeOutcome outcome,
        Func<CancellationToken, MySqlProbeOutcome>? handler = null) : IMySqlBootstrapProbe
    {
        internal int CallCount { get; private set; }
        internal MySqlConnectionStringBuilder? LastConnectionString { get; private set; }
        internal int LastCommandTimeoutSeconds { get; private set; }

        public ValueTask<MySqlProbeOutcome> ProbeAsync(
            MySqlConnectionStringBuilder connectionString,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastConnectionString = connectionString;
            LastCommandTimeoutSeconds = commandTimeoutSeconds;
            return ValueTask.FromResult(handler?.Invoke(cancellationToken) ?? outcome);
        }
    }
}

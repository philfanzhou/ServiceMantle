using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb;
using Xunit;

namespace ServiceMantle.Database.MariaDb.Tests;

public sealed class MariaDbBootstrapDatabaseProviderTests
{
    private const string ValidConnectionString =
        "Server=localhost;Database=app;User ID=app;Password=target-secret";

    [Fact]
    public void Descriptor_is_logically_independent_and_requires_a_server_database_version()
    {
        var descriptor = new MariaDbBootstrapDatabaseProvider().Descriptor;

        Assert.Equal(WellKnownDatabaseProviderIds.MariaDb, descriptor.Id);
        Assert.Equal("MariaDB", descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Required, descriptor.ServerVersionRequirement);
        Assert.Empty(descriptor.Aliases);
        Assert.NotEqual(WellKnownDatabaseProviderIds.MySql, descriptor.Id);
    }

    [Theory]
    [InlineData("10.11")]
    [InlineData("10.11.19")]
    [InlineData("11")]
    [InlineData("11.4")]
    [InlineData("12.3.3")]
    public async Task ValidateAsync_accepts_supported_numeric_versions(string serverVersion)
    {
        var result = await CreateProvider(MariaDbProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11.")]
    [InlineData("11.x")]
    [InlineData("11.4.13.1")]
    [InlineData("11.4.13-MariaDB")]
    public async Task ValidateAsync_rejects_invalid_versions(string serverVersion)
    {
        var result = await CreateProvider(MariaDbProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("9.9")]
    [InlineData("10")]
    [InlineData("10.6")]
    [InlineData("10.10.9")]
    public async Task ValidateAsync_rejects_unsupported_versions(string serverVersion)
    {
        var result = await CreateProvider(MariaDbProbeOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_unsupported", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_rejects_MySql_provider_id_without_probing()
    {
        var probe = new FakeProbe(MariaDbProbeOutcome.Success);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MySql,
            "8.4",
            ValidConnectionString);

        var result = await new MariaDbBootstrapDatabaseProvider(probe).ValidateAsync(
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
        var result = await CreateProvider(MariaDbProbeOutcome.Success).ValidateAsync(
            CreateTarget("11.4", connectionString),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.True(
            result.ErrorCode is "database.connection_string_invalid" or "database.database_required");
    }

    [Fact]
    public async Task ValidateAsync_bounds_connection_and_command_timeouts()
    {
        var probe = new FakeProbe(MariaDbProbeOutcome.Success);
        var target = CreateTarget(
            "11.4",
            ValidConnectionString + ";Connection Timeout=60;Default Command Timeout=90");

        var result = await new MariaDbBootstrapDatabaseProvider(probe).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(8U, probe.LastConnectionString!.ConnectionTimeout);
        Assert.Equal(5U, probe.LastConnectionString.DefaultCommandTimeout);
        Assert.Equal(5, probe.LastCommandTimeoutSeconds);
    }

    public static TheoryData<int, string?> ProbeOutcomes => new()
    {
        { (int)MariaDbProbeOutcome.Success, null },
        { (int)MariaDbProbeOutcome.ServerProductMismatch, "database.provider_validation_failed" },
        { (int)MariaDbProbeOutcome.TargetIdentityMismatch, "database.connection_string_invalid" },
        { (int)MariaDbProbeOutcome.DatabaseNotFound, "database.target_not_found" },
        { (int)MariaDbProbeOutcome.AuthenticationFailed, "database.authentication_failed" },
        { (int)MariaDbProbeOutcome.TargetAccessDenied, "database.permission_denied" },
        { (int)MariaDbProbeOutcome.ConnectionFailed, "database.connection_failed" },
        { (int)MariaDbProbeOutcome.ValidationFailed, "database.provider_validation_failed" },
    };

    [Theory]
    [MemberData(nameof(ProbeOutcomes))]
    public async Task ValidateAsync_maps_probe_outcomes_without_exposing_connection_values(
        int outcomeValue,
        string? errorCode)
    {
        var result = await CreateProvider((MariaDbProbeOutcome)outcomeValue).ValidateAsync(
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
        var probe = new FakeProbe(MariaDbProbeOutcome.Success, _ =>
        {
            source.Cancel();
            throw new InvalidOperationException($"Server=internal;User ID=admin;Password={secret}");
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new MariaDbBootstrapDatabaseProvider(probe)
                .ValidateAsync(CreateTarget(), source.Token)
                .AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Server=internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<MySqlErrorCode, int> ErrorClassifications => new()
    {
        { MySqlErrorCode.UnknownDatabase, (int)MariaDbProbeOutcome.DatabaseNotFound },
        { MySqlErrorCode.AccessDenied, (int)MariaDbProbeOutcome.AuthenticationFailed },
        { MySqlErrorCode.DatabaseAccessDenied, (int)MariaDbProbeOutcome.TargetAccessDenied },
        { MySqlErrorCode.UnableToConnectToHost, (int)MariaDbProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.ConnectionCountError, (int)MariaDbProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.CommandTimeoutExpired, (int)MariaDbProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.QueryTimeout, (int)MariaDbProbeOutcome.ConnectionFailed },
        { MySqlErrorCode.ParseError, (int)MariaDbProbeOutcome.ValidationFailed },
    };

    [Theory]
    [MemberData(nameof(ErrorClassifications))]
    public void Failure_classifier_maps_only_stable_error_categories(
        MySqlErrorCode errorCode,
        int expectedValue)
    {
        Assert.Equal(
            (MariaDbProbeOutcome)expectedValue,
            MariaDbProbeFailureClassifier.Classify(errorCode));
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public void Failure_classifier_maps_transport_exceptions_without_using_their_messages(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(
            MariaDbProbeOutcome.ConnectionFailed,
            MariaDbProbeFailureClassifier.Classify(exception));
    }

    [Theory]
    [InlineData("11.4.13-MariaDB-ubu2404", true)]
    [InlineData("10.11.19-MariaDB", true)]
    [InlineData("8.4.6", false)]
    [InlineData("9.6.0-MySQL", false)]
    [InlineData("", false)]
    public void Server_product_detection_is_MariaDb_specific(string serverVersion, bool expected)
    {
        Assert.Equal(expected, MariaDbDatabaseTarget.IsMariaDbServerVersion(serverVersion));
    }

    [Theory]
    [InlineData(true, false, 0, (int)MariaDbProbeOutcome.Success)]
    [InlineData(false, true, 0, (int)MariaDbProbeOutcome.TargetIdentityMismatch)]
    [InlineData(false, true, 1, (int)MariaDbProbeOutcome.Success)]
    [InlineData(false, true, 2, (int)MariaDbProbeOutcome.Success)]
    [InlineData(false, false, 1, (int)MariaDbProbeOutcome.TargetIdentityMismatch)]
    public void Target_identity_probe_follows_server_database_case_rules(
        bool exactMatch,
        bool caseFoldedMatch,
        int lowerCaseTableNames,
        int expectedValue)
    {
        Assert.Equal(
            (MariaDbProbeOutcome)expectedValue,
            MariaDbBootstrapProbe.ResolveTargetIdentityOutcome(
                exactMatch,
                caseFoldedMatch,
                lowerCaseTableNames));
    }

    private static MariaDbBootstrapDatabaseProvider CreateProvider(MariaDbProbeOutcome outcome) =>
        new(new FakeProbe(outcome));

    private static BootstrapDatabaseConfiguration CreateTarget(
        string serverVersion = "11.4.13",
        string connectionString = ValidConnectionString) =>
        new(WellKnownDatabaseProviderIds.MariaDb, serverVersion, connectionString);

    private sealed class FakeProbe(
        MariaDbProbeOutcome outcome,
        Func<CancellationToken, MariaDbProbeOutcome>? handler = null) : IMariaDbBootstrapProbe
    {
        internal int CallCount { get; private set; }
        internal MySqlConnectionStringBuilder? LastConnectionString { get; private set; }
        internal int LastCommandTimeoutSeconds { get; private set; }

        public ValueTask<MariaDbProbeOutcome> ProbeAsync(
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

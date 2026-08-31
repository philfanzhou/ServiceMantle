using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests;

public sealed class SqlServerBootstrapDatabaseProviderTests
{
    private const string ValidConnectionString =
        "Server=localhost;Initial Catalog=app;User ID=app;Password=target-secret;TrustServerCertificate=true";

    [Fact]
    public void Descriptor_is_independent_and_requires_a_server_database_version()
    {
        var descriptor = new SqlServerBootstrapDatabaseProvider().Descriptor;

        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, descriptor.Id);
        Assert.Equal("SQL Server", descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Required, descriptor.ServerVersionRequirement);
        Assert.Empty(descriptor.Aliases);
        Assert.NotEqual(WellKnownDatabaseProviderIds.PostgreSql, descriptor.Id);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("15.0")]
    [InlineData("16.0.1000")]
    [InlineData("17.0.1000.7")]
    public async Task ValidateAsync_accepts_supported_numeric_versions(string serverVersion)
    {
        var result = await CreateProvider(SqlServerObservationOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("15.")]
    [InlineData("15.x")]
    [InlineData("15.0.1.2.3")]
    [InlineData("15.0-CU")]
    public async Task ValidateAsync_rejects_invalid_versions(string serverVersion)
    {
        var result = await CreateProvider(SqlServerObservationOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("14")]
    [InlineData("14.0.9999.1")]
    public async Task ValidateAsync_rejects_versions_older_than_SQL_Server_2019(string serverVersion)
    {
        var result = await CreateProvider(SqlServerObservationOutcome.Success).ValidateAsync(
            CreateTarget(serverVersion),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_unsupported", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_rejects_other_provider_without_probing()
    {
        var probe = new FakeObservationProbe(SqlServerObservationOutcome.Success);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            ValidConnectionString);

        var result = await new SqlServerBootstrapDatabaseProvider(probe).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_mismatch", result.ErrorCode);
        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    [InlineData("Server==localhost;Initial Catalog=app")]
    [InlineData("Server=localhost;User ID=app;Password=secret")]
    [InlineData("Server=localhost;Initial Catalog=bad\nname;User ID=app")]
    [InlineData("Server=localhost;Initial Catalog=app;AttachDBFilename=/tmp/app.mdf")]
    public async Task ValidateAsync_rejects_invalid_connection_or_database_name(string connectionString)
    {
        var result = await CreateProvider(SqlServerObservationOutcome.Success).ValidateAsync(
            CreateTarget("16.0", connectionString),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.True(
            result.ErrorCode is "database.connection_string_invalid" or "database.database_required");
    }

    [Fact]
    public async Task ValidateAsync_bounds_timeouts_and_disables_connection_retry()
    {
        var probe = new FakeObservationProbe(SqlServerObservationOutcome.Success);
        var target = CreateTarget(
            "16.0",
            ValidConnectionString + ";Connect Timeout=60;Command Timeout=90;Connect Retry Count=5");

        var result = await new SqlServerBootstrapDatabaseProvider(probe).ValidateAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(8, probe.LastConnectionString!.ConnectTimeout);
        Assert.Equal(5, probe.LastConnectionString.CommandTimeout);
        Assert.Equal(0, probe.LastConnectionString.ConnectRetryCount);
    }

    public static TheoryData<int, string?> ProbeOutcomes => new()
    {
        { (int)SqlServerObservationOutcome.Success, null },
        { (int)SqlServerObservationOutcome.TargetIdentityMismatch, "database.connection_string_invalid" },
        { (int)SqlServerObservationOutcome.ServerVersionUnsupported, "database.server_version_unsupported" },
        { (int)SqlServerObservationOutcome.TargetMissing, "database.target_not_found" },
        { (int)SqlServerObservationOutcome.TargetAccessDeniedUnknown, "database.permission_denied" },
        { (int)SqlServerObservationOutcome.TargetAccessDeniedExisting, "database.permission_denied" },
        { (int)SqlServerObservationOutcome.TargetUnavailableExisting, "database.connection_failed" },
        { (int)SqlServerObservationOutcome.AuthenticationFailed, "database.authentication_failed" },
        { (int)SqlServerObservationOutcome.ConnectionFailed, "database.connection_failed" },
        { (int)SqlServerObservationOutcome.ValidationFailed, "database.provider_validation_failed" },
    };

    [Theory]
    [MemberData(nameof(ProbeOutcomes))]
    public async Task ValidateAsync_maps_probe_outcomes_without_exposing_connection_values(
        int outcomeValue,
        string? errorCode)
    {
        var result = await CreateProvider((SqlServerObservationOutcome)outcomeValue).ValidateAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(errorCode is null, result.IsValid);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.DoesNotContain("target-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_cancellation_discards_raw_probe_exception()
    {
        const string secret = "bootstrap-cancel-secret";
        using var source = new CancellationTokenSource();
        var probe = new FakeObservationProbe(SqlServerObservationOutcome.Success, _ =>
        {
            source.Cancel();
            throw new InvalidOperationException($"Server=internal;User ID=admin;Password={secret}");
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SqlServerBootstrapDatabaseProvider(probe)
                .ValidateAsync(CreateTarget(), source.Token)
                .AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<int, int> ErrorClassifications => new()
    {
        { 4060, (int)SqlServerObservationOutcome.TargetAccessDeniedUnknown },
        { 916, (int)SqlServerObservationOutcome.TargetAccessDeniedUnknown },
        { 18456, (int)SqlServerObservationOutcome.AuthenticationFailed },
        { -2, (int)SqlServerObservationOutcome.ConnectionFailed },
        { 53, (int)SqlServerObservationOutcome.ConnectionFailed },
        { 10061, (int)SqlServerObservationOutcome.ConnectionFailed },
        { 102, (int)SqlServerObservationOutcome.ValidationFailed },
    };

    [Theory]
    [MemberData(nameof(ErrorClassifications))]
    public void Failure_classifier_maps_stable_error_categories(int errorNumber, int expectedValue)
    {
        Assert.Equal(
            (SqlServerObservationOutcome)expectedValue,
            SqlServerProbeFailureClassifier.Classify(errorNumber));
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public void Failure_classifier_maps_transport_exception_chains(Type type)
    {
        var inner = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(
            SqlServerObservationOutcome.ConnectionFailed,
            SqlServerProbeFailureClassifier.Classify(new InvalidOperationException("safe", inner)));
    }

    public static TheoryData<bool, string?, byte?, int?, int> MetadataOutcomes => new()
    {
        { true, null, null, null, (int)SqlServerObservationOutcome.TargetMissing },
        { false, null, null, null, (int)SqlServerObservationOutcome.TargetAccessDeniedUnknown },
        { true, "App", 0, 0, (int)SqlServerObservationOutcome.TargetIdentityMismatch },
        { true, "app", 1, 0, (int)SqlServerObservationOutcome.TargetUnavailableExisting },
        { true, "app", 0, 0, (int)SqlServerObservationOutcome.TargetAccessDeniedExisting },
        { true, "app", 0, 1, (int)SqlServerObservationOutcome.TargetUnavailableExisting },
    };

    [Theory]
    [MemberData(nameof(MetadataOutcomes))]
    public void Metadata_interpretation_never_guesses_missing_without_complete_visibility(
        bool hasCompleteVisibility,
        string? visibleDatabaseName,
        byte? databaseState,
        int? hasDatabaseAccess,
        int expectedValue)
    {
        Assert.Equal(
            (SqlServerObservationOutcome)expectedValue,
            SqlServerTargetObservationProbe.InterpretMetadata(
                "app",
                hasCompleteVisibility,
                visibleDatabaseName,
                databaseState,
                hasDatabaseAccess));
    }

    private static SqlServerBootstrapDatabaseProvider CreateProvider(SqlServerObservationOutcome outcome) =>
        new(new FakeObservationProbe(outcome));

    private static BootstrapDatabaseConfiguration CreateTarget(
        string serverVersion = "16.0.1000.6",
        string connectionString = ValidConnectionString) =>
        new(WellKnownDatabaseProviderIds.SqlServer, serverVersion, connectionString);

    private sealed class FakeObservationProbe(
        SqlServerObservationOutcome outcome,
        Func<CancellationToken, SqlServerObservationOutcome>? handler = null)
        : ISqlServerTargetObservationProbe
    {
        internal int CallCount { get; private set; }
        internal SqlConnectionStringBuilder? LastConnectionString { get; private set; }

        public ValueTask<SqlServerObservationOutcome> ObserveAsync(
            SqlConnectionStringBuilder connectionString,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastConnectionString = connectionString;
            return ValueTask.FromResult(handler?.Invoke(cancellationToken) ?? outcome);
        }
    }
}

using System.Net.Sockets;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

public sealed class PostgreSqlBootstrapDatabaseProviderTests
{
    [Fact]
    public void Descriptor_is_configured_for_postgresql()
    {
        var provider = new PostgreSqlBootstrapDatabaseProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.PostgreSql, provider.Descriptor.Id);
        Assert.Equal("PostgreSQL", provider.Descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, provider.Descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Required, provider.Descriptor.ServerVersionRequirement);
        Assert.Empty(provider.Descriptor.Aliases);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("15.4")]
    [InlineData("16")]
    public async Task ValidateAsync_accepts_supported_server_versions(string serverVersion)
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                serverVersion,
                "Host=localhost;Database=signacore;Username=postgres;Password=valid-password"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("15.")]
    [InlineData("15.x")]
    [InlineData("15.4.1")]
    public async Task ValidateAsync_rejects_invalid_server_version(string serverVersion)
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                serverVersion,
                "Host=localhost;Database=signacore;Username=postgres;Password=valid-password"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("14")]
    [InlineData("14.9")]
    public async Task ValidateAsync_rejects_unsupported_server_version(string serverVersion)
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                serverVersion,
                "Host=localhost;Database=signacore;Username=postgres;Password=valid-password"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_unsupported", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_rejects_provider_mismatch()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                "MySQL",
                "16",
                "Host=localhost;Database=signacore;Username=postgres;Password=valid-password"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_rejects_invalid_connection_string()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "15",
                "Host==localhost;Database=signacore;"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.connection_string_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_requires_database_name()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.Success));
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "15",
                "Host=localhost;Username=postgres;Password=valid-password"),
            "validator-master-key");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.database_required", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_success_when_probe_reports_success()
    {
        var probe = new FakeProbe(PostgreSqlProbeOutcome.Success);
        var provider = CreateProvider(probe);
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ValidateAsync_maps_target_identity_mismatch_to_invalid_connection_string()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.TargetIdentityMismatch));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.connection_string_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_maps_target_not_found()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.DatabaseNotFound));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.target_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_maps_authentication_failed()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.AuthenticationFailed));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.authentication_failed", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_maps_connection_failure()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.ConnectionFailed));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("database.connection_failed", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_maps_unexpected_probe_error_as_provider_failure()
    {
        var provider = CreateProvider(new FakeProbe(PostgreSqlProbeOutcome.ValidationFailed));
        const string connectionSecret = "Password=very-secret";
        var candidate = CreateCandidate(
            connectionString: $"Host=localhost;Database=signacore;Username=postgres;{connectionSecret}");

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);
        var serialized = result.ToString();

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_validation_failed", result.ErrorCode);
        Assert.DoesNotContain(connectionSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_maps_postgres_exception_database_not_found()
    {
        var exception = CreatePostgresException("3D000", "Password=pst-secret");
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.DatabaseNotFound, outcome);
    }

    [Fact]
    public void Classifier_maps_postgres_exception_authentication_failure_28p01()
    {
        var exception = CreatePostgresException("28P01", "Password=pst-secret");
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.AuthenticationFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_postgres_exception_authentication_failure_28000()
    {
        var exception = CreatePostgresException("28000", "Password=pst-secret");
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.AuthenticationFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_postgres_exception_insufficient_privilege_as_target_access_denied()
    {
        var exception = CreatePostgresException(PostgresErrorCodes.InsufficientPrivilege, "permission denied");

        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.TargetAccessDenied, outcome);
    }

    [Fact]
    public void Classifier_maps_postgres_exception_connection_failed_class_08xx()
    {
        var exception = CreatePostgresException("08006", "Password=pst-secret");
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.ConnectionFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_npgsql_exception_with_socket_inner_exception_to_connection_failed()
    {
        var exception = new NpgsqlException("npgsql socket failure", new SocketException());
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.ConnectionFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_npgsql_exception_with_timeout_inner_exception_to_connection_failed()
    {
        var exception = new NpgsqlException("npgsql timeout failure", new TimeoutException("timeout"));
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.ConnectionFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_timeout_exception_to_connection_failed()
    {
        var exception = new TimeoutException("connect timeout Password=postgres-top-secret");
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.ConnectionFailed, outcome);
    }

    [Fact]
    public void Classifier_maps_unknown_exception_to_validation_failed()
    {
        const string messageSecret = "Password=validator-secret";
        var exception = new InvalidOperationException(messageSecret);
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);
        var text = outcome.ToString();

        Assert.Equal(PostgreSqlProbeOutcome.ValidationFailed, outcome);
        Assert.DoesNotContain(messageSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_does_not_leak_secret_in_outcome_text()
    {
        const string secret = "Password=leak-prevention";
        var exception = CreatePostgresException("3D000", secret);
        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);

        Assert.Equal(PostgreSqlProbeOutcome.DatabaseNotFound, outcome);
        Assert.DoesNotContain(secret, outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_does_not_leak_probe_exception_secret_in_results()
    {
        const string secret = "Password=exception-leak-test";
        var provider = CreateProvider(new ThrowingProbe(CreatePostgresException("3D000", secret)));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, TestContext.Current.CancellationToken);
        var text = result.ToString();

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_validation_failed", result.ErrorCode);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_propagates_operation_canceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider(new FakeProbe(
            PostgreSqlProbeOutcome.Success,
            token =>
            {
                _ = token;
                throw new OperationCanceledException(token);
            }));
        var candidate = CreateCandidate();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ValidateAsync(candidate.Database, source.Token).AsTask());
    }

    [Fact]
    public async Task ValidateAsync_integration_with_candidate_validator()
    {
        var probe = new FakeProbe(PostgreSqlProbeOutcome.Success);
        var provider = CreateProvider(probe);
        var validator = new BootstrapDatabaseCandidateValidator(
            new BootstrapDatabaseProviderRegistry([provider]));
        var candidate = CreateCandidate("15.4");

        var result = await validator.ValidateAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ValidateAsync_forwards_cancellation_token_to_probe()
    {
        using var source = new CancellationTokenSource();
        var expected = source.Token;
        CancellationToken observed = default;
        var provider = CreateProvider(new FakeProbe(
            PostgreSqlProbeOutcome.Success,
            token =>
            {
                observed = token;
                return PostgreSqlProbeOutcome.Success;
            }));
        var candidate = CreateCandidate();

        var result = await provider.ValidateAsync(candidate.Database, expected);

        Assert.True(result.IsValid);
        Assert.Equal(expected, observed);
    }

    private static PostgreSqlBootstrapDatabaseProvider CreateProvider(
        INpgsqlBootstrapProbe? probe = null)
    {
        return probe is null ? new PostgreSqlBootstrapDatabaseProvider() : new PostgreSqlBootstrapDatabaseProvider(probe);
    }

    private static BootstrapConfiguration CreateCandidate(
        string serverVersion = "15",
        string connectionString = "Host=localhost;Database=signacore;Username=postgres;Password=smoketest-password")
    {
        return new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, serverVersion, connectionString),
                "validator-master-key");
    }

    private static PostgresException CreatePostgresException(
        string sqlState,
        string messageContainsSecret)
    {
        return new PostgresException(
            $"Database validation failed. {messageContainsSecret}",
            "FATAL",
            "FATAL",
            sqlState);
    }

    private sealed class FakeProbe : INpgsqlBootstrapProbe
    {
        private readonly Func<CancellationToken, PostgreSqlProbeOutcome>? handler;
        private readonly PostgreSqlProbeOutcome outcome;

        public FakeProbe(
            PostgreSqlProbeOutcome outcome,
            Func<CancellationToken, PostgreSqlProbeOutcome>? handler = null)
        {
            this.outcome = outcome;
            this.handler = handler;
        }

        public int CallCount { get; private set; }

        public ValueTask<PostgreSqlProbeOutcome> ProbeAsync(
            NpgsqlConnectionStringBuilder connectionString,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _ = connectionString;
            _ = commandTimeoutSeconds;

            if (handler is not null)
            {
                return ValueTask.FromResult(handler(cancellationToken));
            }

            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingProbe : INpgsqlBootstrapProbe
    {
        private readonly Exception exception;

        public ThrowingProbe(Exception exception)
        {
            this.exception = exception;
        }

        public ValueTask<PostgreSqlProbeOutcome> ProbeAsync(
            NpgsqlConnectionStringBuilder connectionString,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            _ = connectionString;
            _ = commandTimeoutSeconds;
            _ = cancellationToken;
            throw exception;
        }
    }
}

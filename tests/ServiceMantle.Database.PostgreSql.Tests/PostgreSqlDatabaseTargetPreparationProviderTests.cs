using Npgsql;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

public sealed class PostgreSqlDatabaseTargetPreparationProviderTests
{
    private const string ValidTargetConnectionString =
        "Host=localhost;Database=app;Username=app;Password=target-secret";
    private const string ValidAdministrativeConnectionString =
        "Host=localhost;Database=postgres;Username=admin;Password=admin-secret";

    [Fact]
    public void ProviderId_and_TargetKind_are_configured_for_postgresql()
    {
        var provider = new PostgreSqlDatabaseTargetPreparationProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.PostgreSql, provider.ProviderId);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, provider.TargetKind);
    }

    [Fact]
    public async Task ObserveAsync_rejects_provider_mismatch()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.Success));
        var target = new BootstrapDatabaseConfiguration("MySQL", "8.0", ValidTargetConnectionString);

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);

        Assert.False(observation.IsServerReachable);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch,
            observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_pre_cancellation_precedes_provider_mismatch()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.Success));
        var target = new BootstrapDatabaseConfiguration("MySQL", "8.0", ValidTargetConnectionString);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ObserveAsync(target, source.Token).AsTask());
    }

    [Fact]
    public async Task ObserveAsync_rejects_invalid_connection_string()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.Success));
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host==localhost;Database=app;");

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);

        Assert.False(observation.IsServerReachable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_pre_cancellation_precedes_invalid_connection_string()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.Success));
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host==localhost;Database=app;");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ObserveAsync(target, source.Token).AsTask());
    }

    [Fact]
    public async Task ObserveAsync_requires_database_name()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.Success));
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host=localhost;Username=app;Password=app-secret");

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);

        Assert.False(observation.IsServerReachable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_maps_success_to_target_connectable()
    {
        var probe = new FakeObservationProbe(PostgreSqlProbeOutcome.Success);
        var provider = CreateProvider(probe);

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.True(observation.IsTargetConnectable);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_maps_target_identity_mismatch_to_invalid_target()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.TargetIdentityMismatch));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_maps_database_not_found_to_target_missing()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.DatabaseNotFound));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.False(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_maps_authentication_failed_to_unknown_target_existence()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.AuthenticationFailed));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_maps_target_access_denied_to_known_existing_target()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.TargetAccessDenied));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_maps_connection_failed_to_server_unreachable()
    {
        var provider = CreateProvider(new FakeObservationProbe(PostgreSqlProbeOutcome.ConnectionFailed));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.False(observation.IsServerReachable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, observation.ErrorCode);
    }

    [Fact]
    public async Task ObserveAsync_propagates_operation_canceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider(new FakeObservationProbe(
            PostgreSqlProbeOutcome.Success,
            token => throw new OperationCanceledException(token)));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ObserveAsync(CreateTarget(), source.Token).AsTask());
    }

    [Fact]
    public async Task ObserveAsync_propagates_cancellation_when_probe_returns_a_failure_outcome()
    {
        using var source = new CancellationTokenSource();
        var provider = CreateProvider(new FakeObservationProbe(
            PostgreSqlProbeOutcome.ConnectionFailed,
            _ =>
            {
                source.Cancel();
                return PostgreSqlProbeOutcome.ConnectionFailed;
            }));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ObserveAsync(CreateTarget(), source.Token).AsTask());

        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ObserveAsync_does_not_leak_secret_from_exception()
    {
        const string secret = "Password=observe-leak-test";
        var provider = CreateProvider(new ThrowingObservationProbe(new InvalidOperationException(secret)));

        var observation = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, observation.ErrorCode);
        Assert.DoesNotContain(secret, observation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_rejects_non_positive_timeout()
    {
        var provider = CreateProvider();
        var request = CreateRequest();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.PrepareAsync(request, TimeSpan.Zero, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task PrepareAsync_rejects_timeout_above_cancellation_source_limit()
    {
        var provider = CreateProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.PrepareAsync(CreateRequest(), TimeSpan.MaxValue, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PrepareAsync_caller_cancellation_precedes_invalid_timeout()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider().PrepareAsync(CreateRequest(), TimeSpan.Zero, source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider().PrepareAsync(CreateRequest(), TimeSpan.MaxValue, source.Token).AsTask());
    }

    [Fact]
    public async Task PrepareAsync_rejects_provider_mismatch()
    {
        var provider = CreateProvider();
        var target = new BootstrapDatabaseConfiguration("MySQL", "8.0", ValidTargetConnectionString);
        var request = new DatabaseTargetPreparationRequest(target, ValidAdministrativeConnectionString);

        var result = await provider.PrepareAsync(request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_requires_database_name_in_target()
    {
        var provider = CreateProvider();
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host=localhost;Username=app;Password=app-secret");
        var request = new DatabaseTargetPreparationRequest(target, ValidAdministrativeConnectionString);

        var result = await provider.PrepareAsync(request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
    }

    [Theory]
    [InlineData("app\nname")]
    public async Task PrepareAsync_rejects_malformed_database_name_before_contacting_server(string databaseName)
    {
        var targetConnectionString = new NpgsqlConnectionStringBuilder(ValidTargetConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                targetConnectionString),
            ValidAdministrativeConnectionString);
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Null(probe.LastDatabaseName);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("éééééééééééééééééééééééééééééééé")]
    public async Task PrepareAsync_defers_database_name_byte_limit_to_server_encoding(string databaseName)
    {
        var targetConnectionString = new NpgsqlConnectionStringBuilder(ValidTargetConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                targetConnectionString),
            ValidAdministrativeConnectionString);
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(databaseName, probe.LastDatabaseName);
    }

    [Fact]
    public async Task PrepareAsync_preserves_valid_quoted_unicode_database_name()
    {
        const string databaseName = "app_\"_数据库";
        var targetConnectionString = new NpgsqlConnectionStringBuilder(ValidTargetConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                targetConnectionString),
            ValidAdministrativeConnectionString);
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(databaseName, probe.LastDatabaseName);
    }

    [Fact]
    public async Task PrepareAsync_rejects_invalid_administrative_connection_string()
    {
        var provider = CreateProvider();
        var request = new DatabaseTargetPreparationRequest(CreateTarget(), "Host==localhost;Database=postgres;");

        var result = await provider.PrepareAsync(request, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_returns_created_when_probe_creates_database()
    {
        var probe = new FakeCreationProbe(
            (name, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)));
        var provider = CreateProvider(creationProbe: probe);

        var result = await provider.PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.Equal("app", probe.LastDatabaseName);
        Assert.Equal("app", probe.LastOwnerName);
    }

    [Fact]
    public async Task PrepareAsync_isolates_administrative_connection_from_pooling_and_ambient_transactions()
    {
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(probe.LastAdministrativeConnectionString!.Pooling);
        Assert.False(probe.LastAdministrativeConnectionString.Enlist);
        Assert.NotNull(probe.LastTargetConnectionString);
        Assert.Equal("postgres", probe.LastTargetConnectionString.Database);
        Assert.Equal("app", probe.LastTargetConnectionString.Username);
        Assert.Equal("target-secret", probe.LastTargetConnectionString.Password);
        Assert.False(probe.LastTargetConnectionString.Pooling);
        Assert.False(probe.LastTargetConnectionString.Enlist);
    }

    [Fact]
    public async Task PrepareAsync_returns_already_exists_without_treating_it_as_failure()
    {
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists)));
        var provider = CreateProvider(creationProbe: probe);

        var result = await provider.PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, result.Outcome);
    }

    [Theory]
    [InlineData(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied)]
    [InlineData(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict)]
    [InlineData(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed)]
    public async Task PrepareAsync_forwards_failure_error_codes_from_probe(string errorCode)
    {
        var probe = new FakeCreationProbe(
            (_, _, _, _) => ValueTask.FromResult(DatabaseTargetPreparationResult.Failure(errorCode)));
        var provider = CreateProvider(creationProbe: probe);

        var result = await provider.PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_maps_timeout_expiry_to_timeout_error_code()
    {
        var probe = new FakeCreationProbe(async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        });
        var provider = CreateProvider(creationProbe: probe);

        var result = await provider.PrepareAsync(
            CreateRequest(),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task PrepareAsync_propagates_caller_cancellation_distinctly_from_timeout()
    {
        using var source = new CancellationTokenSource();
        var probe = new FakeCreationProbe(async (_, _, _, token) =>
        {
            source.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        });
        var provider = CreateProvider(creationProbe: probe);

        // Cancellation may surface as the base type or as TaskCanceledException, depending on
        // whether it is observed at an explicit check or propagated from an awaited Task.Delay.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.PrepareAsync(CreateRequest(), TimeSpan.FromSeconds(30), source.Token).AsTask());
    }

    [Fact]
    public async Task PrepareAsync_caller_cancellation_discards_raw_database_exception()
    {
        const string secret = "admin-cancellation-secret";
        using var source = new CancellationTokenSource();
        var probe = new FakeCreationProbe((_, _, _, _) =>
        {
            source.Cancel();
            throw new NpgsqlException($"Host=internal;Username=admin;Password={secret}");
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateProvider(creationProbe: probe)
                .PrepareAsync(CreateRequest(), TimeSpan.FromSeconds(30), source.Token)
                .AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Host=internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Username=admin", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_does_not_leak_administrative_secret_in_result()
    {
        const string secret = "Password=admin-leak-test";
        var probe = new FakeCreationProbe(
            (_, _, _, _) => throw new InvalidOperationException(secret));
        var provider = CreateProvider(creationProbe: probe);
        var target = CreateTarget();
        var request = new DatabaseTargetPreparationRequest(
            target,
            $"Host=localhost;Database=postgres;Username=admin;{secret}");

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Creation_failure_classifier_maps_wrapped_target_access_denied_to_permission_denied()
    {
        var postgresException = new PostgresException(
            "permission denied",
            "FATAL",
            "FATAL",
            PostgresErrorCodes.InsufficientPrivilege);
        var wrappedException = new NpgsqlException("Exception while connecting", postgresException);

        var errorCode = NpgsqlDatabaseCreationProbe.ClassifyFailure(wrappedException);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, errorCode);
    }

    [Theory]
    [InlineData("Host=first,second", true)]
    [InlineData("Host=first,second", false)]
    [InlineData("Multiplexing=true", true)]
    [InlineData("Multiplexing=true", false)]
    public async Task Unstable_routing_is_rejected_before_creation(string setting, bool administrative)
    {
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, "16",
                ValidTargetConnectionString + (administrative ? "" : ";" + setting)),
            ValidAdministrativeConnectionString + (administrative ? ";" + setting : ""));
        var probe = new FakeCreationProbe((_, _, _, _) => throw new InvalidOperationException("Unexpected creation"));
        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Null(probe.LastDatabaseName);
    }

    [Fact]
    public async Task Cancellation_precedes_a_returned_identity_rejection()
    {
        using var caller = new CancellationTokenSource();
        var probe = new FakeCreationProbe((_, _, _, _) =>
        {
            caller.Cancel();
            return ValueTask.FromResult(DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget));
        });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(creationProbe: probe).PrepareAsync(
                CreateRequest(), TimeSpan.FromSeconds(5), caller.Token).AsTask());
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
    }

    private static PostgreSqlDatabaseTargetPreparationProvider CreateProvider(
        INpgsqlBootstrapProbe? observationProbe = null,
        INpgsqlDatabaseCreationProbe? creationProbe = null) =>
        new(
            observationProbe ?? new FakeObservationProbe(PostgreSqlProbeOutcome.Success),
            creationProbe ?? new FakeCreationProbe(
                (_, _, _, _) => ValueTask.FromResult(
                    DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created))));

    private static BootstrapDatabaseConfiguration CreateTarget() =>
        new(WellKnownDatabaseProviderIds.PostgreSql, "16", ValidTargetConnectionString);

    private static DatabaseTargetPreparationRequest CreateRequest() =>
        new(CreateTarget(), ValidAdministrativeConnectionString);

    private sealed class FakeObservationProbe : INpgsqlBootstrapProbe
    {
        private readonly PostgreSqlProbeOutcome outcome;
        private readonly Func<CancellationToken, PostgreSqlProbeOutcome>? handler;

        public FakeObservationProbe(
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

            return handler is null
                ? ValueTask.FromResult(outcome)
                : ValueTask.FromResult(handler(cancellationToken));
        }
    }

    private sealed class ThrowingObservationProbe : INpgsqlBootstrapProbe
    {
        private readonly Exception exception;

        public ThrowingObservationProbe(Exception exception)
        {
            this.exception = exception;
        }

        public ValueTask<PostgreSqlProbeOutcome> ProbeAsync(
            NpgsqlConnectionStringBuilder connectionString,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class FakeCreationProbe : INpgsqlDatabaseCreationProbe
    {
        private readonly Func<string, string, NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<DatabaseTargetPreparationResult>> handler;

        public FakeCreationProbe(
            Func<string, string, NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<DatabaseTargetPreparationResult>> handler)
        {
            this.handler = handler;
        }

        public NpgsqlConnectionStringBuilder? LastTargetConnectionString { get; private set; }

        public string? LastDatabaseName { get; private set; }

        public string? LastOwnerName { get; private set; }

        public NpgsqlConnectionStringBuilder? LastAdministrativeConnectionString { get; private set; }

        public ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
            string databaseName,
            string ownerName,
            NpgsqlConnectionStringBuilder targetConnectionString,
            NpgsqlConnectionStringBuilder administrativeConnectionString,
            CancellationToken cancellationToken)
        {
            LastTargetConnectionString = targetConnectionString;
            LastDatabaseName = databaseName;
            LastOwnerName = ownerName;
            LastAdministrativeConnectionString = administrativeConnectionString;
            return handler(databaseName, ownerName, administrativeConnectionString, cancellationToken);
        }
    }
}

using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class DatabaseTargetPreparationTests
{
    [Fact]
    public void Observation_server_unreachable_reports_status_and_error_code()
    {
        var observation = DatabaseTargetObservation.ServerUnreachable("database_target_preparation.connection_failed");

        Assert.Equal(DatabaseTargetObservationStatus.ServerUnreachable, observation.Status);
        Assert.False(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal("database_target_preparation.connection_failed", observation.ErrorCode);
    }

    [Fact]
    public void Observation_target_missing_reports_server_reachable_but_no_target()
    {
        var observation = DatabaseTargetObservation.TargetMissing();

        Assert.True(observation.IsServerReachable);
        Assert.False(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);
    }

    [Fact]
    public void Observation_target_unreachable_reports_known_existing_target()
    {
        var observation = DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            targetExists: true);

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal("database_target_preparation.permission_denied", observation.ErrorCode);
    }

    [Fact]
    public void Observation_target_unreachable_can_report_unknown_existence()
    {
        var observation = DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
    }

    [Fact]
    public void Observation_target_connectable_reports_all_signals_true()
    {
        var observation = DatabaseTargetObservation.TargetConnectable();

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.True(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);
    }

    [Fact]
    public void Observation_rejects_empty_error_code_for_failures()
    {
        Assert.Throws<ArgumentException>(() => DatabaseTargetObservation.ServerUnreachable(" "));
        Assert.Throws<ArgumentException>(() => DatabaseTargetObservation.TargetUnreachable(""));
    }

    [Theory]
    [InlineData("Password=admin-secret;Host=db")]
    [InlineData("database_target_preparation.unknown")]
    [InlineData("database_target_preparation.密码")]
    [InlineData("database_target_preparation.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Observation_rejects_unregistered_or_unsafe_error_code(string errorCode)
    {
        Assert.Throws<ArgumentException>(() => DatabaseTargetObservation.ServerUnreachable(errorCode));
        Assert.Throws<ArgumentException>(() => DatabaseTargetObservation.TargetUnreachable(errorCode));
    }

    [Fact]
    public void Observation_ToString_never_includes_secrets()
    {
        const string secret = "Password=super-secret";
        var observation = DatabaseTargetObservation.ServerUnreachable("database_target_preparation.connection_failed");

        Assert.DoesNotContain(secret, observation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Preparation_result_success_reports_outcome()
    {
        var created = DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        var alreadyExists = DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists);

        Assert.True(created.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, created.Outcome);
        Assert.Null(created.ErrorCode);

        Assert.True(alreadyExists.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, alreadyExists.Outcome);
    }

    [Fact]
    public void Preparation_result_failure_reports_error_code()
    {
        var result = DatabaseTargetPreparationResult.Failure(
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);

        Assert.False(result.Succeeded);
        Assert.Null(result.Outcome);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported, result.ErrorCode);
    }

    [Fact]
    public void Preparation_result_rejects_undefined_outcome()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DatabaseTargetPreparationResult.Success((DatabaseTargetPreparationOutcome)99));
    }

    [Fact]
    public void Preparation_result_rejects_empty_error_code()
    {
        Assert.Throws<ArgumentException>(() => DatabaseTargetPreparationResult.Failure(" "));
    }

    [Theory]
    [InlineData("Password=admin-secret;Host=db")]
    [InlineData("database_target_preparation.unknown")]
    [InlineData("database_target_preparation.密码")]
    [InlineData("database_target_preparation.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Preparation_result_rejects_unregistered_or_unsafe_error_code(string errorCode)
    {
        Assert.Throws<ArgumentException>(() => DatabaseTargetPreparationResult.Failure(errorCode));
    }

    [Fact]
    public void Preparation_request_rejects_null_target()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DatabaseTargetPreparationRequest(null!, "Host=admin;Username=admin;Password=admin-secret"));
    }

    [Fact]
    public void Preparation_request_rejects_empty_administrative_connection_string()
    {
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host=db;Database=app;Username=app;Password=app-secret");

        Assert.Throws<ArgumentException>(() => new DatabaseTargetPreparationRequest(target, "  "));
    }

    [Fact]
    public void Preparation_request_ToString_never_includes_either_connection_string()
    {
        const string targetSecret = "Password=target-secret";
        const string administrativeSecret = "Password=admin-secret";
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            $"Host=db;Database=app;Username=app;{targetSecret}");
        var request = new DatabaseTargetPreparationRequest(
            target,
            $"Host=db;Database=postgres;Username=admin;{administrativeSecret}");

        var text = request.ToString();

        Assert.DoesNotContain(targetSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(administrativeSecret, text, StringComparison.Ordinal);
        Assert.Equal($"DatabaseTargetPreparationRequest(Provider={WellKnownDatabaseProviderIds.PostgreSql})", text);
    }

    [Fact]
    public void Registry_resolves_provider_by_id_case_insensitively()
    {
        var provider = new FakeProvider(WellKnownDatabaseProviderIds.PostgreSql);
        var registry = new DatabaseTargetPreparationProviderRegistry([provider], DatabaseProviderIdResolver.Empty);

        var found = registry.TryGetProvider("postgresql", out var resolved);

        Assert.True(found);
        Assert.Same(provider, resolved);
    }

    [Fact]
    public void Registry_reports_capability_not_supported_when_provider_is_not_registered()
    {
        var registry = new DatabaseTargetPreparationProviderRegistry([new FakeProvider(WellKnownDatabaseProviderIds.PostgreSql)], DatabaseProviderIdResolver.Empty);

        var found = registry.TryGetProvider(WellKnownDatabaseProviderIds.Sqlite, out var provider);

        Assert.False(found);
        Assert.Null(provider);
    }

    [Fact]
    public void Registry_lookup_returns_false_for_null_or_empty_id()
    {
        var registry = new DatabaseTargetPreparationProviderRegistry(null, DatabaseProviderIdResolver.Empty);

        Assert.False(registry.TryGetProvider(null, out _));
        Assert.False(registry.TryGetProvider("", out _));
        Assert.False(registry.TryGetProvider("   ", out _));
    }

    [Fact]
    public void Registry_allows_empty_or_null_provider_collection()
    {
        var emptyRegistry = new DatabaseTargetPreparationProviderRegistry([], DatabaseProviderIdResolver.Empty);
        var nullRegistry = new DatabaseTargetPreparationProviderRegistry(null, DatabaseProviderIdResolver.Empty);

        Assert.False(emptyRegistry.TryGetProvider("anything", out _));
        Assert.False(nullRegistry.TryGetProvider("anything", out _));
    }

    [Fact]
    public void Registry_rejects_duplicate_provider_id()
    {
        var first = new FakeProvider("PostgreSQL");
        var second = new FakeProvider("postgresql");

        Assert.Throws<ArgumentException>(() => new DatabaseTargetPreparationProviderRegistry([first, second], DatabaseProviderIdResolver.Empty));
    }

    [Fact]
    public void Registry_normalizes_provider_id_before_duplicate_detection_and_lookup()
    {
        var provider = new FakeProvider(" PostgreSQL ");
        var registry = new DatabaseTargetPreparationProviderRegistry([provider], DatabaseProviderIdResolver.Empty);

        Assert.True(registry.TryGetProvider(" postgresql ", out var resolved));
        Assert.Same(provider, resolved);

        Assert.Throws<ArgumentException>(() =>
            new DatabaseTargetPreparationProviderRegistry(
                [provider, new FakeProvider("postgresql")], DatabaseProviderIdResolver.Empty));
    }

    [Theory]
    [InlineData("invalid provider")]
    [InlineData(".invalid")]
    [InlineData("provider/password")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Registry_rejects_non_canonical_provider_id(string providerId)
    {
        Assert.Throws<ArgumentException>(() =>
            new DatabaseTargetPreparationProviderRegistry([new FakeProvider(providerId)], DatabaseProviderIdResolver.Empty));
    }

    [Fact]
    public void Registry_rejects_undefined_target_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DatabaseTargetPreparationProviderRegistry(
                [new FakeProvider("InvalidKind", (BootstrapDatabaseTargetKind)99)], DatabaseProviderIdResolver.Empty));
    }

    [Fact]
    public void Registry_rejects_null_provider()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DatabaseTargetPreparationProviderRegistry([null!], DatabaseProviderIdResolver.Empty));
    }

    [Fact]
    public void Registry_is_independent_from_bootstrap_provider_registry()
    {
        // Registering a bootstrap validation provider must not imply target preparation support:
        // the two registries are separate types with separate registration tables.
        var bootstrapProviderType = typeof(IBootstrapDatabaseProvider);
        var preparationProviderType = typeof(IDatabaseTargetPreparationProvider);

        Assert.False(bootstrapProviderType.IsAssignableFrom(preparationProviderType));
        Assert.False(preparationProviderType.IsAssignableFrom(bootstrapProviderType));
    }

    private sealed class FakeProvider : IDatabaseTargetPreparationProvider
    {
        public FakeProvider(
            string providerId,
            BootstrapDatabaseTargetKind targetKind = BootstrapDatabaseTargetKind.ServerDatabase)
        {
            ProviderId = providerId;
            TargetKind = targetKind;
        }

        public string ProviderId { get; }

        public BootstrapDatabaseTargetKind TargetKind { get; }

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetObservation.TargetMissing());

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created));
    }
}

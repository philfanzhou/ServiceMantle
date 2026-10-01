using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Unit tests for the maintenance connection derivation. No container is required.
/// </summary>
public sealed class PostgreSqlMaintenanceConnectionTests
{
    [Fact]
    public void Derivation_changes_only_the_database()
    {
        const string target =
            "Host=db.example;Port=5432;Database=app;Username=app;Password=secret;MaxAutoPrepare=10";

        var maintenance = PostgreSqlMaintenanceConnection.DeriveConnectionString(target);
        var builder = new NpgsqlConnectionStringBuilder(maintenance);

        Assert.Equal(PostgreSqlMaintenanceConnection.MaintenanceDatabaseName, builder.Database);
        Assert.Equal("db.example", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("app", builder.Username);
        Assert.Equal("secret", builder.Password);
        Assert.Equal(10, builder.MaxAutoPrepare);
    }

    [Fact]
    public void Derivation_replaces_an_already_maintenance_database_name()
    {
        const string target = "Host=db.example;Database=postgres;Username=app;Password=secret";

        var maintenance = PostgreSqlMaintenanceConnection.DeriveConnectionString(target);

        Assert.Equal(
            PostgreSqlMaintenanceConnection.MaintenanceDatabaseName,
            new NpgsqlConnectionStringBuilder(maintenance).Database);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Derivation_rejects_empty_input(string connectionString)
    {
        Assert.Throws<ArgumentException>(() =>
            PostgreSqlMaintenanceConnection.DeriveConnectionString(connectionString));
    }

    [Fact]
    public void Derivation_rejects_an_unparsable_connection_string_without_echoing_it()
    {
        const string invalid = "Host==broken;Password=top-secret-value";

        var exception = Assert.Throws<ArgumentException>(() =>
            PostgreSqlMaintenanceConnection.DeriveConnectionString(invalid));

        Assert.DoesNotContain("top-secret-value", exception.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Real PostgreSQL startup database gate tests using Testcontainers: the fixed gate sequence over
/// the real preparation provider. Enabled via RUN_SERVICEMANTLE_POSTGRES_TESTS=true and docker
/// availability.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class StartupDatabaseGatePostgreSqlTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private NpgsqlConnectionStringBuilder? serverConnectionInfo;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        var image = GetPostgresImage();
        container = new PostgreSqlBuilder(image)
            .WithPassword("gate-password")
            .WithUsername("gate-admin")
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        serverConnectionInfo = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Existing_target_is_observed_connectable_and_migrates_without_preparation()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"gate_existing_{UniqueSuffix()}";
        await CreateRealDatabaseAsync(databaseName);
        var harness = CreateHarness(databaseName, enableTargetPreparation: true);
        var result = await harness.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        Assert.Equal(1, harness.PreparationObserveCount);
        Assert.Equal(0, harness.PreparationPrepareCount);
    }

    [Fact]
    public async Task Missing_target_with_creation_permitted_is_prepared_and_migrates()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"gate_create_{UniqueSuffix()}";
        var harness = CreateHarness(
            databaseName,
            enableTargetPreparation: true,
            allowTargetCreation: true,
            useDerivedMaintenanceConnection: true);
        var result = await harness.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, harness.PreparationObserveCount);
        Assert.Equal(1, harness.PreparationPrepareCount);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
    }

    [Fact]
    public async Task Missing_target_without_creation_permission_fails_with_creation_not_allowed()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var harness = CreateHarness(
            $"gate_denied_{UniqueSuffix()}",
            enableTargetPreparation: true,
            allowTargetCreation: false);
        var result = await harness.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.CreationNotAllowed,
            result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(1, harness.PreparationObserveCount);
        Assert.Equal(0, harness.PreparationPrepareCount);
        // The missing target was never created.
        Assert.False(await DatabaseExistsAsync(harness.DatabaseName));
    }

    [Fact]
    public async Task Unreachable_server_fails_with_connection_failed()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var harness = CreateHarness(
            $"gate_unreachable_{UniqueSuffix()}",
            enableTargetPreparation: true,
            allowTargetCreation: true,
            portOverride: ClosedPort);
        var result = await harness.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(0, harness.PreparationPrepareCount);
    }

    [Fact]
    public async Task Authentication_failure_fails_with_authentication_failed()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var harness = CreateHarness(
            $"gate_auth_{UniqueSuffix()}",
            enableTargetPreparation: true,
            allowTargetCreation: true,
            passwordOverride: "wrong-password");
        var result = await harness.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(0, harness.PreparationPrepareCount);
    }

    [Fact]
    public async Task Prepared_but_still_unconnectable_target_fails_with_not_connectable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        // The real provider cannot produce a prepared-but-unconnectable target, so this path uses
        // a scripted provider in front of the same registries to keep the gate's classification
        // honest against real assembly types.
        var harness = CreateHarness(
            $"gate_recheck_{UniqueSuffix()}",
            enableTargetPreparation: true,
            allowTargetCreation: true,
            scriptedPreparation: new NeverConnectablePreparation());
        var result = await harness.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.NotConnectableAfterPreparation,
            result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        Assert.Equal(0, harness.ExecutorInspectCount);
    }

    private sealed class NeverConnectablePreparation : IDatabaseTargetPreparationProvider
    {
        private int observeCalls;

        public string ProviderId => WellKnownDatabaseProviderIds.PostgreSql;
        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // First observation reports a missing target so the gate prepares it; the
            // post-preparation re-observation never becomes connectable.
            var observation = Interlocked.Increment(ref observeCalls) == 1
                ? DatabaseTargetObservation.TargetMissing()
                : DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed);
            return ValueTask.FromResult(observation);
        }

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created));
    }

    private sealed class CompatibleExecutor : IDatabaseMigrationExecutor
    {
        public int InspectCount;

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref InspectCount);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible);
        }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CountingPreparationProvider(
        PostgreSqlDatabaseTargetPreparationProvider inner) : IDatabaseTargetPreparationProvider
    {
        public int ObserveCount;
        public int PrepareCount;

        public string ProviderId => inner.ProviderId;
        public BootstrapDatabaseTargetKind TargetKind => inner.TargetKind;

        public async ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ObserveCount);
            return await inner.ObserveAsync(target, cancellationToken);
        }

        public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PrepareCount);
            return await inner.PrepareAsync(request, timeout, cancellationToken);
        }
    }

    private sealed class PostgreSqlCapability : IDatabaseDeploymentCapabilityProvider
    {
        public DatabaseDeploymentCapability Capability { get; } = new(
            WellKnownDatabaseProviderIds.PostgreSql,
            DatabaseDeploymentSupport.SingleAndMultiInstance);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) => ValueTask.FromResult("gate-postgresql");
    }

    private sealed class ProviderStub(CompatibleExecutor executor) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDatabaseMigrationExecutor) ? executor : null;
    }

    private sealed class ScopeFactoryStub(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ScopeStub(provider);

        private sealed class ScopeStub(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
    }

    private sealed class GateHarness(
        StartupDatabaseGate gate,
        StartupDatabaseGateOptions options,
        CountingPreparationProvider preparation,
        CompatibleExecutor executor,
        string databaseName)
    {
        public StartupDatabaseGate Gate { get; } = gate;
        public StartupDatabaseReceipt Receipt { get; } = new();
        public StartupDatabaseGateOptions Options { get; } = options;
        public CountingPreparationProvider Preparation { get; } = preparation;
        public CompatibleExecutor Executor { get; } = executor;
        public string DatabaseName { get; } = databaseName;

        public int PreparationObserveCount => Preparation.ObserveCount;
        public int PreparationPrepareCount => Preparation.PrepareCount;
        public int ExecutorInspectCount => Executor.InspectCount;

        public ValueTask<StartupDatabaseGateResult> RunAsync() =>
            Gate.RunAsync(
                Options, Receipt, ServiceId.Parse("gate-postgres"), CancellationToken.None);
    }

    private GateHarness CreateHarness(
        string databaseName,
        bool enableTargetPreparation,
        bool allowTargetCreation = false,
        bool useDerivedMaintenanceConnection = false,
        int? portOverride = null,
        string? passwordOverride = null,
        IDatabaseTargetPreparationProvider? scriptedPreparation = null)
    {
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildTargetConnectionString(databaseName, portOverride, passwordOverride));

        string? maintenance = null;
        if (useDerivedMaintenanceConnection)
        {
            maintenance = PostgreSqlMaintenanceConnection.DeriveConnectionString(
                target.ConnectionString);
        }
        else if (allowTargetCreation)
        {
            maintenance = BuildMaintenanceConnectionString(portOverride);
        }

        var options = new StartupDatabaseGateOptions(
            target,
            DatabaseDeploymentMode.MultiInstance,
            TimeSpan.FromSeconds(30),
            enableTargetPreparation,
            allowTargetCreation,
            maintenance);

        var executor = new CompatibleExecutor();
        var preparation = new CountingPreparationProvider(
            new PostgreSqlDatabaseTargetPreparationProvider());
        var preparationProvider =
            scriptedPreparation as IDatabaseTargetPreparationProvider ?? preparation;

        var gate = new StartupDatabaseGate(
            new DatabaseDeploymentCapabilityRegistry(
                [new PostgreSqlCapability()],
                DatabaseProviderIdResolver.Empty),
            new DatabaseTargetPreparationProviderRegistry(
                [preparationProvider], DatabaseProviderIdResolver.Empty),
            new DatabaseMigrationLockProviderRegistry(
                [new PostgreSqlMigrationLockProvider()],
                DatabaseProviderIdResolver.Empty),
            new ScopeFactoryStub(new ProviderStub(executor)));

        return new GateHarness(gate, options, preparation, executor, databaseName);
    }

    private async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(null));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)";
        command.Parameters.AddWithValue("@name", databaseName);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is true;
    }

    private async Task CreateRealDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(null));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private string BuildMaintenanceConnectionString(int? portOverride) =>
        BuildTargetConnectionString(
            PostgreSqlMaintenanceConnection.MaintenanceDatabaseName,
            portOverride,
            passwordOverride: null);

    private string BuildTargetConnectionString(
        string databaseName,
        int? portOverride,
        string? passwordOverride) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = serverConnectionInfo!.Host,
            Port = portOverride ?? serverConnectionInfo.Port,
            Database = databaseName,
            Username = serverConnectionInfo.Username,
            Password = passwordOverride ?? serverConnectionInfo.Password!,
            Pooling = false,
        }.ConnectionString;

    private static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    // A TCP port that is closed on the loopback interface in every supported environment;
    // neighboring Testcontainers ports can be occupied by sibling containers in a parallel run.
    private const int ClosedPort = 1;

    private bool CanRun => ShouldRunPostgreSqlTests() && serverConnectionInfo is not null;

    private const string SkipReason = "PostgreSQL tests disabled or container not initialized.";

    private static bool ShouldRunPostgreSqlTests()
    {
        var envVar = Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS");
        return envVar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static string GetPostgresImage()
    {
        var envVar = Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE");
        return envVar ?? "postgres:15-alpine";
    }
}

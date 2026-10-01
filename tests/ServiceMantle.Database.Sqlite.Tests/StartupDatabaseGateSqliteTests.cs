using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

/// <summary>
/// Startup database gate tests over the real SQLite target preparation provider: deployment
/// validation precedes any file-system side effect, and the single-instance-only capability fails
/// closed for MultiInstance before the file is created.
/// </summary>
public sealed class StartupDatabaseGateSqliteTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        AppContext.BaseDirectory,
        $"gate-sqlite-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Single_instance_gate_observes_and_migrates_the_target()
    {
        var harness = CreateHarness(DatabaseDeploymentMode.SingleInstance);
        var result = await harness.RunAsync();

        Assert.True(result.Succeeded, $"gate failed: {result?.ErrorCode} / {harness.Receipt.ErrorCode}");
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        // Preparation is an explicit switch: disabled means zero preparation calls.
        Assert.Equal(0, harness.Preparation.ObserveCount);
        Assert.Equal(0, harness.Preparation.PrepareCount);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task MultiInstance_fails_closed_with_lock_not_supported_before_any_file_side_effect()
    {
        var harness = CreateHarness(DatabaseDeploymentMode.MultiInstance);
        var result = await harness.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(ServiceMigrationReadinessState.Failed, harness.Receipt.State);
        // Deployment validation refused from declarations alone: no observation, no file.
        Assert.Equal(0, harness.Preparation.ObserveCount);
        Assert.Equal(0, harness.Preparation.PrepareCount);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task Enabled_preparation_creates_the_missing_file_target_when_permitted()
    {
        var harness = CreateHarness(
            DatabaseDeploymentMode.SingleInstance,
            enableTargetPreparation: true,
            allowTargetCreation: true);
        var result = await harness.RunAsync();

        Assert.True(result.Succeeded, $"gate failed: {result?.ErrorCode} / {harness.Receipt.ErrorCode}");
        Assert.Equal(2, harness.Preparation.ObserveCount);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, harness.Receipt.State);
        Assert.Equal(1, harness.Preparation.PrepareCount);
        Assert.True(File.Exists(databasePath));
    }

    public void Dispose()
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private SqliteGateHarness CreateHarness(
        DatabaseDeploymentMode mode,
        bool enableTargetPreparation = false,
        bool allowTargetCreation = false) =>
        new(databasePath, mode, enableTargetPreparation, allowTargetCreation);

    private sealed class CountingSqlitePreparation(
        SqliteDatabaseTargetPreparationProvider inner) : IDatabaseTargetPreparationProvider
    {
        public int ObserveCount { get; private set; }
        public int PrepareCount { get; private set; }

        public string ProviderId => inner.ProviderId;
        public BootstrapDatabaseTargetKind TargetKind => inner.TargetKind;

        public async ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            ObserveCount++;
            return await inner.ObserveAsync(target, cancellationToken);
        }

        public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            return await inner.PrepareAsync(request, timeout, cancellationToken);
        }
    }

    private sealed class CompatibleExecutor : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible);
        }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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

    private sealed class SqliteGateHarness(
        string databasePath,
        DatabaseDeploymentMode mode,
        bool enableTargetPreparation,
        bool allowTargetCreation)
    {
        public StartupDatabaseReceipt Receipt { get; } = new();
        public CountingSqlitePreparation Preparation { get; } =
            new(new SqliteDatabaseTargetPreparationProvider());

        public ValueTask<StartupDatabaseGateResult> RunAsync()
        {
            var target = new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.Sqlite,
                null,
                $"Data Source={databasePath}");
            var options = new StartupDatabaseGateOptions(
                target,
                mode,
                TimeSpan.FromSeconds(5),
                enableTargetPreparation,
                allowTargetCreation,
                maintenanceConnectionString: null);
            var gate = new StartupDatabaseGate(
                new DatabaseDeploymentCapabilityRegistry(
                    [new SqliteDatabaseTargetPreparationProvider()],
                    DatabaseProviderIdResolver.Empty),
                new DatabaseTargetPreparationProviderRegistry(
                    [Preparation], DatabaseProviderIdResolver.Empty),
                new DatabaseMigrationLockProviderRegistry(
                    [], DatabaseProviderIdResolver.Empty),
                new ScopeFactoryStub(new ProviderStub(new CompatibleExecutor())));

            return gate.RunAsync(
                options, Receipt, ServiceId.Parse("gate-sqlite"), CancellationToken.None);
        }
    }
}

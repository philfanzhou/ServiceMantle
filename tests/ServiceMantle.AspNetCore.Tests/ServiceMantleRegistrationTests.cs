using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.Bootstrap;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleRegistrationTests
{
    [Fact]
    public void AddServiceMantle_registers_identity_bootstrap_phase_and_migration_foundation()
    {
        var services = new ServiceCollection();
        var serviceId = ServiceId.Parse("catalog");
        var instanceId = InstanceId.Parse("catalog-01");
        var bootstrapPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        services.AddServiceMantle(serviceId, instanceId, bootstrapPath);

        using var provider = services.BuildServiceProvider();

        Assert.Same(serviceId, provider.GetRequiredService<ServiceId>());
        Assert.Same(instanceId, provider.GetRequiredService<InstanceId>());
        Assert.Equal(
            Path.GetFullPath(bootstrapPath),
            provider.GetRequiredService<BootstrapFileStore>().FilePath);
        Assert.NotNull(provider.GetRequiredService<BootstrapConfigurationManager>());
        Assert.Empty(provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().Descriptors);
        Assert.NotNull(provider.GetRequiredService<DatabaseMigrationLockProviderRegistry>());
        Assert.NotNull(provider.GetRequiredService<IServiceStartupPhaseResolver>());
        Assert.Null(provider.GetService<DatabaseMigrationOrchestrator>());
    }

    [Fact]
    public void AddServiceMantle_is_idempotent_for_the_same_host_registration()
    {
        var services = new ServiceCollection();
        var serviceId = ServiceId.Parse("catalog");
        var instanceId = InstanceId.Parse("catalog-01");
        var bootstrapPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        services.AddServiceMantle(serviceId, instanceId, bootstrapPath);
        services.AddServiceMantle(serviceId, instanceId, bootstrapPath);

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ServiceId>());
        Assert.Single(provider.GetServices<InstanceId>());
        Assert.Single(provider.GetServices<BootstrapFileStore>());
    }

    [Fact]
    public void AddServiceMantle_rejects_conflicting_identity_or_bootstrap_registration()
    {
        var services = new ServiceCollection();
        var serviceId = ServiceId.Parse("catalog");
        var instanceId = InstanceId.Parse("catalog-01");
        var bootstrapPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        services.AddServiceMantle(serviceId, instanceId, bootstrapPath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceMantle(
                ServiceId.Parse("billing"),
                instanceId,
                bootstrapPath));

        Assert.DoesNotContain("billing", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bootstrapPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddServiceMantle_rejects_pre_registered_host_owned_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(ServiceId.Parse("catalog"));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01")));
    }

    [Fact]
    public void Builder_composes_provider_extensions_without_driver_references()
    {
        var services = new ServiceCollection();

        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<FakeBootstrapProvider>()
            .AddMigrationLockProvider<FakeMigrationLockProvider>()
            .AddDatabaseMigration<FakeMigrationExecutor>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        var bootstrapRegistry = provider.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        Assert.True(bootstrapRegistry.TryGetProvider("fake", out var bootstrapProvider));
        Assert.IsType<FakeBootstrapProvider>(bootstrapProvider);

        var migrationRegistry = provider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();
        Assert.True(migrationRegistry.TryGetProvider("fake", out var lockProvider));
        Assert.IsType<FakeMigrationLockProvider>(lockProvider);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DatabaseMigrationOrchestrator>());
    }

    [Fact]
    public async Task Minimal_web_service_can_register_build_start_and_stop()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddServiceMantle(
            ServiceId.Parse("minimal-web"),
            InstanceId.Parse("minimal-web-test"));

        await using var application = builder.Build();
        application.MapGet("/", () => "ok");

        await application.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.NotEmpty(application.Urls);
            Assert.NotNull(application.Services.GetRequiredService<ServiceId>());
        }
        finally
        {
            await application.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeBootstrapProvider : IBootstrapDatabaseProvider
    {
        public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
            "fake",
            "Fake",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Optional);

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapDatabaseConfiguration database,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(BootstrapValidationResult.Success());
    }

    [Fact]
    public void Di_bootstrap_store_canonicalizes_an_alias_registered_after_AddServiceMantle()
    {
        // The store must be built from the final registry snapshot, not from the empty snapshot
        // that exists while AddServiceMantle runs.
        var services = new ServiceCollection();
        var serviceId = ServiceId.Parse("catalog");
        var directory = Path.Combine(Path.GetTempPath(), "ServiceMantle.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bootstrapPath = Path.Combine(directory, "catalog.bootstrap.json");

        try
        {
            services
                .AddServiceMantle(serviceId, InstanceId.Parse("catalog-01"), bootstrapPath)
                .AddBootstrapDatabaseProvider<AliasedProvider>();

            using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<BootstrapFileStore>();

            store.Create(new BootstrapConfiguration(
                serviceId,
                new BootstrapDatabaseConfiguration("aliased-db", "16", "Host=db;Password=p"),
                "master-key"));

            using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Assert.Equal(
                "AliasedProvider",
                document.RootElement.GetProperty("Database").GetProperty("Provider").GetString());
            Assert.Equal("AliasedProvider", store.Load().Database.Provider);
            Assert.Equal("AliasedProvider", provider.GetRequiredService<BootstrapConfigurationManager>()
                .GetStatus().Provider);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Di_bootstrap_store_and_lock_registry_share_one_resolver_snapshot()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json"))
            .AddBootstrapDatabaseProvider<AliasedProvider>()
            .AddMigrationLockProvider<AliasedMigrationLockProvider>();

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>(),
            provider.GetRequiredService<BootstrapFileStore>().ProviderRegistry);
        Assert.Same(
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver,
            provider.GetRequiredService<BootstrapFileStore>().ProviderIdResolver);

        // The lock provider registered itself under the canonical id; the alias must find it.
        Assert.True(provider.GetRequiredService<DatabaseMigrationLockProviderRegistry>()
            .TryGetProvider("aliased-db", out var lockProvider));
        Assert.NotNull(lockProvider);
    }

    private sealed class AliasedProvider : IBootstrapDatabaseProvider
    {
        public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
            "AliasedProvider",
            "Aliased Provider",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Optional,
            aliases: ["aliased-db"]);

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapDatabaseConfiguration database,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(BootstrapValidationResult.Success());
    }

    private sealed class AliasedMigrationLockProvider : IDatabaseMigrationLockProvider
    {
        public string ProviderId => "AliasedProvider";

        public ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IDatabaseMigrationLock>(new FakeMigrationLock());
    }

    private sealed class FakeMigrationLockProvider : IDatabaseMigrationLockProvider
    {
        public string ProviderId => "fake";

        public ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IDatabaseMigrationLock>(new FakeMigrationLock());
    }

    private sealed class FakeMigrationLock : IDatabaseMigrationLock
    {
        public string ProviderId => "fake";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMigrationExecutor : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible);

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public sealed class StartupDatabaseGateServiceRegistrationTests
{
    [Fact]
    public void Direct_registration_is_lazy_idempotent_and_has_no_host_options_or_identity()
    {
        var services = new ServiceCollection();
        var constructions = 0;
        services.AddScoped<IDatabaseMigrationExecutor>(_ => { constructions++; return new Executor(); });
        Assert.Same(services, services.AddServiceMantleStartupDatabaseGateServices());
        services.AddServiceMantleStartupDatabaseGateServices();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<StartupDatabaseGate>());
        Assert.Equal(ServiceMigrationReadinessState.NotStarted, provider.GetRequiredService<StartupDatabaseReceipt>().State);
        Assert.Equal(0, constructions);
        Assert.Null(provider.GetService<StartupDatabaseGateOptions>());
        Assert.Null(provider.GetService<ServiceId>());
        Assert.DoesNotContain(services, d => d.ServiceType.Name == "IHostedService");
        foreach (var type in new[] { typeof(BootstrapDatabaseProviderRegistry), typeof(DatabaseDeploymentCapabilityRegistry),
            typeof(DatabaseTargetPreparationProviderRegistry), typeof(DatabaseMigrationLockProviderRegistry),
            typeof(StartupDatabaseReceipt), typeof(StartupDatabaseGate) })
            Assert.Single(services.Where(d => d.ServiceType == type));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Borrowed_instances_survive_but_DI_owned_scoped_executors_are_released(bool owned, bool fail)
    {
        var executor = new Executor(fail);
        var services = new ServiceCollection();
        services.AddServiceMantleStartupDatabaseGateServices();
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(new DeclaredCapability());
        if (owned) services.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        else services.AddSingleton<IDatabaseMigrationExecutor>(executor);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<StartupDatabaseGate>().RunAsync(
            new(new("CustomDb", "1", "opaque"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5)),
            provider.GetRequiredService<StartupDatabaseReceipt>(), ServiceId.Parse("direct-gate"),
            TestContext.Current.CancellationToken);
        Assert.Equal(!fail, result.Succeeded);
        Assert.Equal(owned ? 1 : 0, executor.Disposals);
        await provider.DisposeAsync();
        Assert.Equal(owned ? 1 : 0, executor.Disposals);
        if (!owned) await executor.DisposeAsync();
    }

    [Fact]
    public async Task Missing_executor_and_precancel_keep_existing_failure_semantics()
    {
        var services = new ServiceCollection();
        services.AddServiceMantleStartupDatabaseGateServices();
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(new DeclaredCapability());
        using var provider = services.BuildServiceProvider();
        var options = new StartupDatabaseGateOptions(new("CustomDb", "1", "opaque"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5));
        var gate = provider.GetRequiredService<StartupDatabaseGate>();
        var receipt = new StartupDatabaseReceipt();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(options, receipt, ServiceId.Parse("direct-gate"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(WellKnownMigrationErrorCodes.ExecutionFailed, receipt.ErrorCode);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        receipt = new();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => gate.RunAsync(options, receipt, ServiceId.Parse("direct-gate"), cts.Token).AsTask());
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(ServiceMigrationReadinessState.Running, receipt.State);
    }

    [Fact]
    public void Null_collection_is_rejected() => Assert.Throws<ArgumentNullException>(() =>
        ServiceMantleStartupDatabaseGateServiceCollectionExtensions.AddServiceMantleStartupDatabaseGateServices(null!));

    private sealed class DeclaredCapability : IDatabaseDeploymentCapabilityProvider
    {
        private readonly string identity = Guid.NewGuid().ToString();
        public DatabaseDeploymentCapability Capability { get; } = new("CustomDb", DatabaseDeploymentSupport.SingleInstanceOnly);
        public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken cancellationToken) => ValueTask.FromResult(identity);
    }

    private sealed class Executor(bool fail = false) : IDatabaseMigrationExecutor, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fail ? MigrationObservationState.InspectionFailed : MigrationObservationState.CurrentVersionCompatible);
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}

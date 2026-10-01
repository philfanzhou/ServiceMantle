using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using ServiceMantle.Web.Hosting;
using Xunit;

namespace ServiceMantle.Web.Tests;

public sealed class StartupDatabaseGateRegistrationTests
{
    private const string Secret = "web-gate-secret";
    private static readonly ServiceId Service = ServiceId.Parse("web-gate");
    private static readonly InstanceId Instance = InstanceId.Parse("web-gate-01");

    private static BootstrapDatabaseConfiguration Target(
        string provider = "CustomDb",
        string connectionString = "target=one;Password=" + Secret) =>
        new(provider, "1", connectionString);

    private static StartupDatabaseGateOptions Options(
        DatabaseDeploymentMode mode = DatabaseDeploymentMode.SingleInstance,
        bool enableTargetPreparation = false,
        bool allowTargetCreation = false,
        string? maintenanceConnectionString = null,
        BootstrapDatabaseConfiguration? database = null) =>
        new(
            database ?? Target(),
            mode,
            TimeSpan.FromSeconds(5),
            enableTargetPreparation,
            allowTargetCreation,
            maintenanceConnectionString);

    [Fact]
    public void AddStartupDatabaseGate_registers_the_gate_receipt_and_hosted_entry()
    {
        using var provider = BuildProvider(Options());

        Assert.NotNull(provider.GetRequiredService<StartupDatabaseGate>());
        Assert.NotNull(provider.GetRequiredService<StartupDatabaseReceipt>());
        Assert.NotNull(provider.GetRequiredService<StartupDatabaseGateOptions>());
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is StartupDatabaseGateHostedService);
        Assert.NotNull(provider.GetRequiredService<DatabaseDeploymentCapabilityRegistry>());
        // The hosted entry resolves the same shared gate, receipt, and options singletons.
        var hosted = provider.GetServices<IHostedService>().OfType<StartupDatabaseGateHostedService>().Single();
        Assert.NotNull(hosted);
    }

    [Fact]
    public void AddStartupDatabaseGate_is_idempotent_for_equivalent_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddServiceMantle(Service, Instance)
            .AddDatabaseMigration<ConnectableExecutor>();
        var options = Options();
        builder.AddStartupDatabaseGate(options);
        builder.AddStartupDatabaseGate(options);

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<StartupDatabaseGateOptions>());
        Assert.Single(
            provider.GetServices<IHostedService>().OfType<StartupDatabaseGateHostedService>());
    }

    [Fact]
    public void Without_the_gate_no_startup_database_services_are_registered()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(Service, Instance);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<StartupDatabaseReceipt>());
        Assert.Null(provider.GetService<StartupDatabaseGate>());
        Assert.DoesNotContain(
            provider.GetServices<IHostedService>(),
            service => service is StartupDatabaseGateHostedService);
    }

    public static IEnumerable<object[]> SharedScenarios()
    {
        yield return new object[]
        {
            new SharedScenario(
                Name: "succeeds",
                Options: Options(),
                CapabilitySupport: DatabaseDeploymentSupport.SingleInstanceOnly,
                Preparation: new ConnectablePreparation(),
                ExpectedSucceeded: true,
                ExpectedErrorCode: null),
        };
        yield return new object[]
        {
            new SharedScenario(
                Name: "deployment-validation-fails",
                Options: Options(DatabaseDeploymentMode.MultiInstance),
                CapabilitySupport: DatabaseDeploymentSupport.SingleInstanceOnly,
                Preparation: new ConnectablePreparation(),
                ExpectedSucceeded: false,
                ExpectedErrorCode: WellKnownMigrationErrorCodes.LockNotSupported),
        };
        yield return new object[]
        {
            new SharedScenario(
                Name: "preparation-refused",
                Options: Options(enableTargetPreparation: true),
                CapabilitySupport: DatabaseDeploymentSupport.SingleInstanceOnly,
                Preparation: new RefusedPreparation(),
                ExpectedSucceeded: false,
                ExpectedErrorCode: WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
        };
    }

    [Theory]
    [MemberData(nameof(SharedScenarios))]
    public async Task Hosted_and_direct_entries_report_identical_results(SharedScenario scenario)
    {
        // The hosted entry and the direct entry share one implementation; both must report the
        // same receipt state, error code, and success flag for the same scenario.
        var hostedProvider = BuildProvider(
            scenario.Options, scenario.CapabilitySupport, scenario.Preparation);
        var directProvider = BuildProvider(
            scenario.Options, scenario.CapabilitySupport, scenario.Preparation);

        var hostedService = hostedProvider.GetServices<IHostedService>()
            .OfType<StartupDatabaseGateHostedService>()
            .Single();
        if (scenario.ExpectedSucceeded)
        {
            await hostedService.StartingAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            var hostedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => hostedService.StartingAsync(TestContext.Current.CancellationToken));
            Assert.Contains(scenario.ExpectedErrorCode!, hostedFailure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, hostedFailure.Message, StringComparison.Ordinal);
        }

        var hostedReceipt = hostedProvider.GetRequiredService<StartupDatabaseReceipt>();
        Assert.Equal(
            scenario.ExpectedSucceeded ? ServiceMigrationReadinessState.Succeeded : ServiceMigrationReadinessState.Failed,
            hostedReceipt.State);
        Assert.Equal(scenario.ExpectedErrorCode, hostedReceipt.ErrorCode);
        Assert.NotNull(hostedService.Result);
        Assert.Equal(scenario.ExpectedSucceeded, hostedService.Result.Succeeded);

        var gate = directProvider.GetRequiredService<StartupDatabaseGate>();
        var directResult = await gate.RunAsync(
            scenario.Options,
            directProvider.GetRequiredService<StartupDatabaseReceipt>(),
            directProvider.GetRequiredService<ServiceId>(),
            TestContext.Current.CancellationToken);
        var directReceipt = directProvider.GetRequiredService<StartupDatabaseReceipt>();

        Assert.Equal(scenario.ExpectedSucceeded, directResult.Succeeded);
        Assert.Equal(scenario.ExpectedErrorCode, directResult.ErrorCode);
        Assert.Equal(hostedReceipt.State, directReceipt.State);
        Assert.Equal(hostedReceipt.ErrorCode, directReceipt.ErrorCode);
    }

    [Fact]
    public async Task Hosted_entry_cancellation_stops_the_host_startup_with_the_caller_token()
    {
        using var provider = BuildProvider(Options());
        var hostedService = provider.GetServices<IHostedService>()
            .OfType<StartupDatabaseGateHostedService>()
            .Single();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hostedService.StartingAsync(cts.Token));
        Assert.Null(hostedService.Result);
        Assert.Equal(ServiceMigrationReadinessState.Running, provider.GetRequiredService<StartupDatabaseReceipt>().State);
    }

    private static ServiceProvider BuildProvider(
        StartupDatabaseGateOptions options,
        DatabaseDeploymentSupport capabilitySupport = DatabaseDeploymentSupport.SingleInstanceOnly,
        IDatabaseTargetPreparationProvider? preparation = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddServiceMantle(Service, Instance)
            .AddDatabaseMigration<ConnectableExecutor>();
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(
            new WebCapabilityProvider("CustomDb", capabilitySupport));
        if (preparation is not null)
        {
            services.AddSingleton<IDatabaseTargetPreparationProvider>(sp => preparation);
        }

        builder.AddStartupDatabaseGate(options);
        return services.BuildServiceProvider();
    }

    public sealed record SharedScenario(
        string Name,
        StartupDatabaseGateOptions Options,
        DatabaseDeploymentSupport CapabilitySupport,
        IDatabaseTargetPreparationProvider Preparation,
        bool ExpectedSucceeded,
        string? ExpectedErrorCode);

    private sealed class WebCapabilityProvider(
        string provider,
        DatabaseDeploymentSupport support) : IDatabaseDeploymentCapabilityProvider
    {
        public DatabaseDeploymentCapability Capability { get; } = new(provider, support);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) => ValueTask.FromResult("canonical-target");
    }

    /// <summary>An executor that reports empty, applies nothing, and verifies compatible.</summary>
    private sealed class ConnectableExecutor : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible);
        }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectablePreparation : IDatabaseTargetPreparationProvider
    {
        public string ProviderId => "CustomDb";
        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetObservation.TargetConnectable());

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created));
    }

    private sealed class RefusedPreparation : IDatabaseTargetPreparationProvider
    {
        public string ProviderId => "CustomDb";
        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetObservation.TargetUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed));

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed));
    }
}

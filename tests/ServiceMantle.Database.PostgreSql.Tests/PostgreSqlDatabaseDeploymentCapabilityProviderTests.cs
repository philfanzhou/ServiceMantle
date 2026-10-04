using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

public sealed class PostgreSqlDatabaseDeploymentCapabilityProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static BootstrapDatabaseConfiguration Target(string connection = "Host=example;Database=app", string provider = "PostgreSql") => new(provider, "16", connection);
    private static ValueTask<string> Identity(string connection) => new PostgreSqlDatabaseDeploymentCapabilityProvider().GetCanonicalTargetIdentityAsync(Target(connection), Token);

    [Theory]
    [InlineData("host=EXAMPLE;database=app;port=5432;username=one;password=secret;timeout=4")]
    [InlineData("Database=app;Host= example ;Password=changed;Pooling=false")]
    public async Task Equivalent_spellings_and_credentials_keep_the_same_identity(string connection) =>
        Assert.Equal(await Identity("Host=example;Database=app"), await Identity(connection));

    [Theory]
    [InlineData("Host=other;Database=app")]
    [InlineData("Host=example;Port=5433;Database=app")]
    [InlineData("Host=example;Database=App")]
    [InlineData("Host=example;Database=other")]
    public async Task Supported_distinct_targets_stay_distinct(string connection) =>
        Assert.NotEqual(await Identity("Host=example;Database=app"), await Identity(connection));

    [Theory]
    [InlineData("Password=secret")]
    [InlineData("Host=example;Username=app")]
    [InlineData("Host=one,two;Database=app")]
    [InlineData("Host=/tmp/socket;Database=app")]
    [InlineData("Host=example;Database=app;Password=secret;Unknown=secret")]
    [InlineData("Host=example;Port=invalid;Database=app")]
    public async Task Unsupported_or_invalid_shapes_return_empty_and_never_enter_executor(string connection)
    {
        var provider = new PostgreSqlDatabaseDeploymentCapabilityProvider();
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target(connection), Token));
        var executor = new Executor();
        var result = await Orchestrator(executor, provider).OrchestrateMigrationAsync(ServiceId.Parse("pg-deployment"),
            Target(connection), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(2), Token);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(0, executor.Calls);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Explicit_registration_is_idempotent_and_does_not_imply_other_capabilities()
    {
        var services = new ServiceCollection();
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDatabaseDeploymentCapabilityProvider));
        services.AddServiceMantlePostgreSqlDeploymentCapability().AddServiceMantlePostgreSqlDeploymentCapability();
        Assert.Single(services);
        using var container = services.BuildServiceProvider();
        var provider = Assert.Single(container.GetServices<IDatabaseDeploymentCapabilityProvider>());
        Assert.Equal(DatabaseDeploymentSupport.SingleAndMultiInstance, provider.Capability.Support);
        Assert.Equal(WellKnownDatabaseProviderIds.PostgreSql, provider.Capability.ProviderId);
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target(provider: "Sqlite"), Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetCanonicalTargetIdentityAsync(Target("Host=example;Database=app;Password=secret"), cancelled.Token).AsTask());
        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(provider));
        Assert.Throws<ArgumentNullException>(() => ServiceMantlePostgreSqlDeploymentCapabilityServiceCollectionExtensions.AddServiceMantlePostgreSqlDeploymentCapability(null!));
    }

    [Fact]
    public async Task Same_target_across_registries_serializes_but_other_targets_progress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Executor(async () => { entered.SetResult(); await release.Task.WaitAsync(Token); });
        var firstRun = Orchestrator(first, new PostgreSqlDatabaseDeploymentCapabilityProvider()).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target(), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token).AsTask();
        await entered.Task.WaitAsync(Token);
        var second = new Executor();
        var secondRun = Orchestrator(second, new PostgreSqlDatabaseDeploymentCapabilityProvider()).OrchestrateMigrationAsync(
            ServiceId.Parse("other-service"), Target("Host=EXAMPLE;Port=5432;Database=app;Password=changed"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token).AsTask();
        var other = new Executor();
        Assert.True((await Orchestrator(other, new PostgreSqlDatabaseDeploymentCapabilityProvider()).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target("Host=example;Database=other"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token)).Succeeded);
        Assert.Equal(0, second.Calls);
        release.SetResult();
        Assert.True((await firstRun).Succeeded);
        Assert.True((await secondRun).Succeeded);
    }

    [Fact]
    public async Task Multi_instance_uses_lock_and_does_not_parse_an_unsupported_identity_shape()
    {
        var executor = new Executor();
        var locking = new LockProvider();
        var result = await Orchestrator(executor, new PostgreSqlDatabaseDeploymentCapabilityProvider(), locking).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target("Host=one,two;Database=app"), DatabaseDeploymentMode.MultiInstance, TimeSpan.FromSeconds(5), Token);
        Assert.True(result.Succeeded);
        Assert.Equal(1, locking.Calls);
        Assert.True(locking.Released);
    }

    private static DatabaseMigrationOrchestrator Orchestrator(Executor executor, IDatabaseDeploymentCapabilityProvider provider, params IDatabaseMigrationLockProvider[] locks) =>
        new(executor, new(locks, DatabaseProviderIdResolver.Empty), new([provider], DatabaseProviderIdResolver.Empty));
    private sealed class Executor(Func<Task>? inspect = null) : IDatabaseMigrationExecutor
    {
        public int Calls { get; private set; }
        public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        { Calls++; if (inspect is not null) await inspect(); return MigrationObservationState.CurrentVersionCompatible; }
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class LockProvider : IDatabaseMigrationLockProvider
    {
        public int Calls { get; private set; }
        public bool Released { get; private set; }
        public string ProviderId => "PostgreSql";
        public ValueTask<IDatabaseMigrationLock> AcquireAsync(ServiceId serviceId, BootstrapDatabaseConfiguration bootstrap, TimeSpan acquireTimeout, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult<IDatabaseMigrationLock>(new Lease(this)); }
        private sealed class Lease(LockProvider owner) : IDatabaseMigrationLock
        {
            public string ProviderId => "PostgreSql";
            public CancellationToken LeaseLost => CancellationToken.None;
            public ValueTask DisposeAsync() { owner.Released = true; return ValueTask.CompletedTask; }
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

public sealed class MySqlDatabaseDeploymentCapabilityProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static BootstrapDatabaseConfiguration Target(string connection = "Server=example;Database=app", string provider = "MySql") => new(provider, "16", connection);
    private static ValueTask<string> Identity(string connection) => Provider().GetCanonicalTargetIdentityAsync(Target(connection), Token);

    [Theory]
    [InlineData("server=EXAMPLE;database=app;port=3306;user id=one;password=secret;connection timeout=4")]
    [InlineData("Database=app;Server= example ;Password=changed;Pooling=false")]
    public async Task Equivalent_spellings_and_credentials_keep_the_same_identity(string connection) =>
        Assert.Equal(await Identity("Server=example;Database=app"), await Identity(connection));

    [Theory]
    [InlineData("Server=other;Database=app")]
    [InlineData("Server=example;Port=3307;Database=app")]
    [InlineData("Server=example;Database=App")]
    [InlineData("Server=example;Database=other")]
    public async Task Supported_distinct_targets_stay_distinct(string connection) =>
        Assert.NotEqual(await Identity("Server=example;Database=app"), await Identity(connection));

    [Theory]
    [InlineData("Password=secret")]
    [InlineData("Server=example;User ID=app")]
    [InlineData("Server=one,two;Database=app")]
    [InlineData("Server=/tmp/socket;Database=app")]
    [InlineData("Server=example;Database=app;Password=secret;Unknown=secret")]
    [InlineData("Server=example;Port=invalid;Database=app")]
    public async Task Unsupported_or_invalid_shapes_return_empty_and_never_enter_executor(string connection)
    {
        var provider = Provider();
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
        services.AddServiceMantleMySqlDeploymentCapability().AddServiceMantleMySqlDeploymentCapability();
        Assert.Single(services);
        using var container = services.BuildServiceProvider();
        var provider = Assert.Single(container.GetServices<IDatabaseDeploymentCapabilityProvider>());
        Assert.Equal(DatabaseDeploymentSupport.SingleAndMultiInstance, provider.Capability.Support);
        Assert.Equal(WellKnownDatabaseProviderIds.MySql, provider.Capability.ProviderId);
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target(provider: "Sqlite"), Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetCanonicalTargetIdentityAsync(Target("Server=example;Database=app;Password=secret"), cancelled.Token).AsTask());
        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(provider));
        Assert.Throws<ArgumentNullException>(() => ServiceMantleMySqlDeploymentCapabilityServiceCollectionExtensions.AddServiceMantleMySqlDeploymentCapability(null!));
    }

    [Fact]
    public async Task Same_target_across_registries_serializes_but_other_targets_progress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Executor(async () => { entered.SetResult(); await release.Task.WaitAsync(Token); });
        var firstRun = Orchestrator(first, Provider()).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target(), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token).AsTask();
        await entered.Task.WaitAsync(Token);
        var second = new Executor();
        var secondRun = Orchestrator(second, Provider()).OrchestrateMigrationAsync(
            ServiceId.Parse("other-service"), Target("Server=EXAMPLE;Port=3306;Database=app;Password=changed"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token).AsTask();
        var other = new Executor();
        Assert.True((await Orchestrator(other, Provider()).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target("Server=example;Database=other"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5), Token)).Succeeded);
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
        var result = await Orchestrator(executor, Provider(), locking).OrchestrateMigrationAsync(
            ServiceId.Parse("pg-deployment"), Target("Server=one,two;Database=app"), DatabaseDeploymentMode.MultiInstance, TimeSpan.FromSeconds(5), Token);
        Assert.True(result.Succeeded);
        Assert.Equal(1, locking.Calls);
        Assert.True(locking.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_failure_and_budget_timeout_map_through_core_without_executor(bool timeout)
    {
        var provider = new MySqlDatabaseDeploymentCapabilityProvider(new Probe(async (_, token) =>
        {
            if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Password=secret");
        }));
        var executor = new Executor();
        var result = await Orchestrator(executor, provider).OrchestrateMigrationAsync(ServiceId.Parse("mysql-budget"),
            Target(), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromMilliseconds(50), Token);
        Assert.Equal(timeout ? WellKnownMigrationErrorCodes.LockTimeout : WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.Equal(0, executor.Calls);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    private static MySqlDatabaseDeploymentCapabilityProvider Provider() => new(new Probe());
    private sealed class Probe(Func<MySqlConnector.MySqlConnectionStringBuilder, CancellationToken, ValueTask<MySqlTargetMetadata>>? callback = null) : IMySqlCanonicalTargetProbe
    {
        public int Calls { get; private set; }
        public ValueTask<MySqlTargetMetadata> ReadAsync(MySqlConnector.MySqlConnectionStringBuilder builder, CancellationToken token)
        {
            Calls++;
            Assert.False(builder.Pooling); Assert.False(builder.AutoEnlist);
            return callback is null ? ValueTask.FromResult(new MySqlTargetMetadata(0, builder.Database, builder.Database.ToLowerInvariant())) : callback(builder, token);
        }
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Server_case_rules_determine_database_alias_equivalence(int rule, bool equal)
    {
        var probe = new Probe((builder, token) => ValueTask.FromResult(new MySqlTargetMetadata(rule, builder.Database, builder.Database.ToLowerInvariant())));
        var provider = new MySqlDatabaseDeploymentCapabilityProvider(probe);
        var first = await provider.GetCanonicalTargetIdentityAsync(Target("Server=example;Database=App"), Token);
        var second = await provider.GetCanonicalTargetIdentityAsync(Target(), Token);
        Assert.Equal(equal, first == second);
        Assert.Equal(2, probe.Calls);
    }
    [Theory]
    [InlineData(-1, "app", "app")]
    [InlineData(3, "app", "app")]
    [InlineData(0, null, "app")]
    [InlineData(1, "app", null)]
    public async Task Unknown_or_empty_metadata_fails_closed(int rule, string? name, string? lower)
    {
        var provider = new MySqlDatabaseDeploymentCapabilityProvider(new Probe((_, _) => ValueTask.FromResult(new MySqlTargetMetadata(rule, name, lower))));
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target(), Token));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Probe_completion_and_cleanup_cancellation_are_sanitized(bool callerCancel, bool internalCancel)
    {
        using var source = new CancellationTokenSource();
        var probe = new Probe((_, _) =>
        {
            if (callerCancel) source.Cancel();
            if (internalCancel) throw new OperationCanceledException("Password=secret");
            throw new InvalidOperationException("release failed Password=secret");
        });
        var provider = new MySqlDatabaseDeploymentCapabilityProvider(probe);
        if (callerCancel)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), source.Token).AsTask());
            Assert.Equal(source.Token, exception.CancellationToken); Assert.Null(exception.InnerException);
            Assert.DoesNotContain("secret", exception.ToString());
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), source.Token).AsTask());
            Assert.Null(exception.InnerException); Assert.DoesNotContain("secret", exception.ToString());
        }
    }
    [Fact]
    public async Task Cancellation_after_successful_metadata_and_unsupported_input_is_observed()
    {
        using var source = new CancellationTokenSource();
        var provider = new MySqlDatabaseDeploymentCapabilityProvider(new Probe((builder, _) =>
        { source.Cancel(); return ValueTask.FromResult(new MySqlTargetMetadata(0, builder.Database, builder.Database)); }));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetCanonicalTargetIdentityAsync(Target(), source.Token).AsTask());
        Assert.Equal(source.Token, exception.CancellationToken);
        var probe = new Probe(); provider = new(probe);
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target("Server=one,two;Database=app"), Token));
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target("Server=example;Database=app;ConnectionProtocol=UnixSocket"), Token));
        Assert.Equal(0, probe.Calls);
        Assert.Equal("SELECT @@lower_case_table_names, DATABASE(), LOWER(DATABASE())", MySqlCanonicalTargetProbe.Query);
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
        public string ProviderId => "MySql";
        public ValueTask<IDatabaseMigrationLock> AcquireAsync(ServiceId serviceId, BootstrapDatabaseConfiguration bootstrap, TimeSpan acquireTimeout, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult<IDatabaseMigrationLock>(new Lease(this)); }
        private sealed class Lease(LockProvider owner) : IDatabaseMigrationLock
        {
            public string ProviderId => "MySql";
            public CancellationToken LeaseLost => CancellationToken.None;
            public ValueTask DisposeAsync() { owner.Released = true; return ValueTask.CompletedTask; }
        }
    }
}

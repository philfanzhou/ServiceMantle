using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantlePhaseGateTests
{
    private const string Secret = "Password=database-secret;SELECT private_data";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static IEnumerable<object[]> Matrix =>
        from phase in Enum.GetValues<ServiceStartupPhase>()
        from migration in Enum.GetValues<ServiceMigrationReadinessState>()
        from database in Enum.GetValues<ServiceDatabaseReadinessState>()
        select new object[] { phase, migration, database };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Complete_phase_migration_database_matrix_is_enforced(ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration, ServiceDatabaseReadinessState database)
    {
        var source = new MutableSource(new(phase, migration, database));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source), health: true);
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        var notMigrating = migration is ServiceMigrationReadinessState.NotStarted or ServiceMigrationReadinessState.Succeeded;
        var migrated = migration == ServiceMigrationReadinessState.Succeeded;
        var reachable = database == ServiceDatabaseReadinessState.Reachable;
        var cases = new Dictionary<string, bool>
        {
            ["/management/bootstrap"] = phase == ServiceStartupPhase.BootstrapConfiguration && notMigrating,
            ["/management/setup"] = phase == ServiceStartupPhase.PendingSetup && migrated && reachable,
            ["/management/settings"] = phase == ServiceStartupPhase.Completed && migrated && reachable,
            ["/business"] = phase == ServiceStartupPhase.Completed && migrated && reachable,
            ["/management/status"] = true,
            ["/health/live"] = true
        };
        foreach (var (path, allowed) in cases)
        {
            using var response = await client.GetAsync(path, Token);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            if (!allowed) Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", await response.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task Minimal_host_needs_no_optional_provider_and_only_status_is_available_without_state()
    {
        await using var app = Build();
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var status = await client.GetAsync("/management/status", Token);
        using var business = await client.GetAsync("/business", Token);
        using var missing = await client.GetAsync("/not-mapped", Token);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, business.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Prefix_normalization_equivalent_registration_and_route_group_share_one_namespace()
    {
        var source = new MutableSource(new(ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            options => options.ManagementPathPrefix = " /OPS/Admin/ ", repeatPrefix: "/ops/admin");
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var allowed = await client.GetAsync("/OPS/ADMIN/setup", Token);
        using var old = await client.GetAsync("/management/setup", Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("relative")]
    [InlineData("/management//")]
    [InlineData("/manage//ment")]
    [InlineData("/health")]
    [InlineData("/HEALTH/live")]
    [InlineData("/manage/../admin")]
    [InlineData("/%2fsecret")]
    [InlineData("/admin?Password=secret")]
    [InlineData("/admin#secret")]
    [InlineData("/admin\\secret")]
    [InlineData("/管理")]
    public async Task Invalid_or_health_conflicting_prefix_fails_startup_safely(string prefix)
    {
        await using var app = Build(configure: options => options.ManagementPathPrefix = prefix, map: false);
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync(Token));
        Assert.Equal("The ServiceMantle phase gate configuration or endpoint mapping is invalid.", exception.Message);
    }

    [Theory]
    [InlineData("conflicting_registration")]
    [InlineData("missing_use")]
    [InlineData("duplicate_use")]
    [InlineData("wrong_surface")]
    [InlineData("unknown_surface")]
    [InlineData("unmarked_management")]
    [InlineData("status_post")]
    [InlineData("dynamic_management_child")]
    [InlineData("duplicate_route")]
    public async Task Configuration_or_endpoint_contract_conflicts_fail_startup(string scenario)
    {
        await using var app = Build(map: false, useCount: scenario == "missing_use" ? 0 : scenario == "duplicate_use" ? 2 : 1,
            repeatPrefix: scenario == "conflicting_registration" ? "/other" : null);
        if (scenario == "wrong_surface") app.MapGet("/management/setup", () => "ok").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Bootstrap);
        if (scenario == "unknown_surface") app.MapGet("/management/setup", () => "ok").WithServiceMantleManagementSurface((ServiceMantleManagementSurface)999);
        if (scenario == "unmarked_management") app.MapGet("/management/unclassified", () => "ok");
        if (scenario == "status_post") app.MapPost("/management/status", () => "ok").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Status);
        if (scenario == "dynamic_management_child") app.MapGet("/management/{**rest}", () => "ok").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management);
        if (scenario == "duplicate_route")
        {
            app.MapGet("/management/settings", () => "first").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management);
            app.MapGet("/MANAGEMENT/settings/", () => "second").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management);
        }
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync(Token));
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("null")]
    [InlineData("timeout")]
    public async Task State_source_failure_timeout_and_internal_cancellation_fail_closed(string mode)
    {
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(_ =>
            mode == "factory" ? throw new InvalidOperationException(Secret) : new FailureSource(mode)),
            options => options.SnapshotTimeout = TimeSpan.FromMilliseconds(50));
        await app.StartAsync(Token);
        using var response = await app.GetTestClient().GetAsync("/business", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", await response.Content.ReadAsStringAsync(Token));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/management/unknown")]
    [InlineData("/MANAGEMENT/bootstrap/unknown")]
    public async Task Unclassified_fallback_routes_cannot_enter_management_namespace_even_when_ready(string path)
    {
        var source = new MutableSource(new(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        app.MapGet("/{**rest}", () => "fallback-secret");
        await app.StartAsync(Token);
        using var response = await app.GetTestClient().GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, source.Calls);
        Assert.DoesNotContain("fallback-secret", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(30001)]
    public async Task Invalid_timeout_fails_startup(double milliseconds)
    {
        await using var app = Build(configure: options => options.SnapshotTimeout = TimeSpan.FromMilliseconds(milliseconds), map: false);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync(Token));
    }

    [Fact]
    public async Task Health_readiness_samples_once_and_unmarked_health_named_route_cannot_bypass()
    {
        var source = new MutableSource(new(ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Unreachable));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source), health: true);
        app.MapGet("/health/custom", () => "protected");
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var ready = await client.GetAsync("/health/ready", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(1, source.Calls);
        using var custom = await client.GetAsync("/health/custom", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, custom.StatusCode);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_single_snapshot_and_next_request_observes_transition()
    {
        var source = new BarrierSource();
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        var pending = Enumerable.Range(0, 12).Select(_ => client.GetAsync("/business", Token)).ToArray();
        await source.AllStarted.Task.WaitAsync(Token);
        source.Current = new(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        source.Release.TrySetResult();
        foreach (var response in await Task.WhenAll(pending))
        {
            using (response) Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        using var after = await client.GetAsync("/business", Token);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(13, source.Calls);
    }

    [Fact]
    public async Task Request_cancellation_propagates_and_never_executes_endpoint()
    {
        var source = new BarrierSource(expectedCalls: 1);
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        await app.StartAsync(Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = app.GetTestClient().GetAsync("/business", cancellation.Token);
        await source.AllStarted.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        source.Release.TrySetResult();
    }

    private static WebApplication Build(Action<IServiceCollection>? services = null,
        Action<ServiceMantlePhaseGateOptions>? configure = null, bool health = false, bool map = true,
        int useCount = 1, string? repeatPrefix = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var service = builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));
        service.AddServiceMantlePhaseGate(configure);
        if (repeatPrefix is not null) service.AddServiceMantlePhaseGate(options => options.ManagementPathPrefix = repeatPrefix);
        if (health) service.AddServiceMantleHealthEndpoints();
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseRouting();
        for (var index = 0; index < useCount; index++) app.UseServiceMantlePhaseGate();
        if (map)
        {
            var management = app.MapServiceMantleManagementGroup();
            management.MapGet("/status", () => "status").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Status);
            management.MapGet("/bootstrap", () => "bootstrap").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Bootstrap);
            management.MapGet("/setup", () => "setup").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Setup);
            management.MapGet("/settings", () => "settings").WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management);
        }
        if (health) app.MapServiceMantleHealthEndpoints();
        app.MapGet("/business", () => "business");
        return app;
    }

    private sealed class MutableSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(snapshot);
        }
    }
    private sealed class FailureSource(string mode) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => mode switch
        {
            "null" => ValueTask.FromResult<ServiceHealthSnapshot>(null!),
            "cancel" => throw new OperationCanceledException(Secret),
            "timeout" => new(new TaskCompletionSource<ServiceHealthSnapshot>().Task),
            _ => throw new InvalidOperationException(Secret)
        };
    }
    private sealed class BarrierSource(int expectedCalls = 12) : IServiceHealthSnapshotSource
    {
        private int calls;
        private ServiceHealthSnapshot current = new(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable);
        public ServiceHealthSnapshot Current { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = Current;
            if (Interlocked.Increment(ref calls) == expectedCalls) AllStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
    }
}

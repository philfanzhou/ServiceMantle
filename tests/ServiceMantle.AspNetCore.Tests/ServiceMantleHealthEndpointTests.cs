using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleHealthEndpointTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    [Fact]
    public async Task Live_is_always_200_and_never_resolves_the_snapshot_source()
    {
        await using (var unregisteredApplication = await StartAsync())
        {
            using var unregisteredResponse = await unregisteredApplication.GetTestClient().GetAsync(
                "/health/live",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, unregisteredResponse.StatusCode);
        }

        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(_ =>
                throw new InvalidOperationException("connection-secret")));
        using var response = await application.GetTestClient().GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(["status"], document.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.Equal("live", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Snapshot_source_contract_exposes_only_one_read_operation()
    {
        var methods = typeof(IServiceHealthSnapshotSource).GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal(nameof(IServiceHealthSnapshotSource.GetSnapshotAsync), method.Name);
        Assert.Equal(typeof(ValueTask<ServiceHealthSnapshot>), method.ReturnType);
        Assert.Equal([typeof(CancellationToken)], method.GetParameters().Select(item => item.ParameterType));
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Ready_endpoints_sample_once_and_map_ready_or_not_ready(string path)
    {
        var source = new SequenceSource(
            ReadySnapshot,
            new ServiceHealthSnapshot(
                ServiceStartupPhase.PendingSetup,
                ServiceMigrationReadinessState.NotStarted,
                ServiceDatabaseReadinessState.Reachable,
                "installation.pending"));
        await using var application = await StartAsync(services => services.AddSingleton<
            IServiceHealthSnapshotSource>(source));
        var client = application.GetTestClient();

        using var ready = await client.GetAsync(path, TestContext.Current.CancellationToken);
        using var notReady = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(2, source.CallCount);
        using var readyJson = await ReadJsonAsync(ready);
        AssertHealthResponse(
            readyJson.RootElement,
            "ready",
            "completed",
            "succeeded",
            "reachable",
            errorCode: null);
        using var notReadyJson = await ReadJsonAsync(notReady);
        AssertHealthResponse(
            notReadyJson.RootElement,
            "not_ready",
            "pendingSetup",
            "notStarted",
            "reachable",
            "installation.pending");
    }

    [Fact]
    public async Task Missing_source_and_source_exception_fail_closed_without_sensitive_material()
    {
        await using (var missingApplication = await StartAsync())
        {
            using var missing = await missingApplication.GetTestClient().GetAsync(
                "/health/ready",
                TestContext.Current.CancellationToken);
            await AssertProbeFailureAsync(
                missing,
                WellKnownServiceHealthErrorCodes.ProbeFailed,
                "connection-secret");
        }

        const string secret =
            "Server=db.internal;Port=1433;User Id=admin;Password=secret;SELECT * FROM schema;Migration_42";
        await using var failingApplication = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(new ThrowingSource(secret)));
        using var failed = await failingApplication.GetTestClient().GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);

        await AssertProbeFailureAsync(
            failed,
            WellKnownServiceHealthErrorCodes.ProbeFailed,
            secret,
            "db.internal",
            "1433",
            "admin",
            "SELECT",
            "Migration_42");
    }

    [Fact]
    public async Task Internal_timeout_is_bounded_and_has_a_distinct_safe_code()
    {
        var source = new NeverCompletingSource();
        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            options => options.ProbeTimeout = ServiceMantleHealthOptions.MinimumProbeTimeout);
        var started = DateTimeOffset.UtcNow;

        using var response = await application.GetTestClient().GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.CallCount);
        Assert.True(source.ObservedToken.CanBeCanceled);
        await AssertProbeFailureAsync(
            response,
            WellKnownServiceHealthErrorCodes.ProbeTimeout,
            "never-completing-secret");
    }

    [Fact]
    public async Task Request_cancellation_propagates_instead_of_returning_503()
    {
        var source = new CancellationSource();
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var cancellation = new CancellationTokenSource();
        var request = application.GetTestClient().GetAsync("/health/ready", cancellation.Token);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(source.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Concurrent_requests_publish_only_their_own_immutable_snapshot()
    {
        var source = new AlternatingSource();
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        var client = application.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await client.GetAsync(
                "/health",
                TestContext.Current.CancellationToken);
            return (
                response.StatusCode,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }));

        Assert.Equal(20, source.CallCount);
        Assert.Equal(10, responses.Count(item => item.StatusCode == HttpStatusCode.OK));
        Assert.Equal(10, responses.Count(item => item.StatusCode == HttpStatusCode.ServiceUnavailable));
        Assert.All(responses, item =>
        {
            using var document = JsonDocument.Parse(item.Item2);
            var status = document.RootElement.GetProperty("status").GetString();
            var phase = document.RootElement.GetProperty("phase").GetString();
            Assert.True(
                status == "ready" && phase == "completed" ||
                status == "not_ready" && phase == "bootstrapConfiguration");
        });
    }

    [Theory]
    [InlineData(99)]
    [InlineData(31_000)]
    public async Task Invalid_probe_timeout_fails_when_the_host_starts(int milliseconds)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-invalid"))
            .AddServiceMantleHealthEndpoints(options =>
                options.ProbeTimeout = TimeSpan.FromMilliseconds(milliseconds));
        await using var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Mapping_requires_explicit_health_registration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("health"),
            InstanceId.Parse("health-unregistered"));
        using var application = builder.Build();

        Assert.Throws<InvalidOperationException>(() =>
            application.MapServiceMantleHealthEndpoints());
    }

    private static async Task<WebApplication> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<ServiceMantleHealthOptions>? configureHealth = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-test"))
            .AddServiceMantleHealthEndpoints(configureHealth);
        configureServices?.Invoke(builder.Services);
        var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

    private static void AssertHealthResponse(
        JsonElement root,
        string status,
        string? phase,
        string? migration,
        string? database,
        string? errorCode)
    {
        Assert.Equal(
            ["status", "phase", "migrationStatus", "databaseStatus", "errorCode"],
            root.EnumerateObject().Select(item => item.Name));
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(phase, root.GetProperty("phase").GetString());
        Assert.Equal(migration, root.GetProperty("migrationStatus").GetString());
        Assert.Equal(database, root.GetProperty("databaseStatus").GetString());
        Assert.Equal(errorCode, root.GetProperty("errorCode").GetString());
    }

    private static async Task AssertProbeFailureAsync(
        HttpResponseMessage response,
        string errorCode,
        params string[] forbidden)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        AssertHealthResponse(
            document.RootElement,
            "not_ready",
            phase: null,
            migration: null,
            database: null,
            errorCode);
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class SequenceSource(params ServiceHealthSnapshot[] snapshots)
        : IServiceHealthSnapshotSource
    {
        private int callCount;

        internal int CallCount => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref callCount) - 1;
            return ValueTask.FromResult(snapshots[index]);
        }
    }

    private sealed class ThrowingSource(string message) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ServiceHealthSnapshot>(new InvalidOperationException(message));
    }

    private sealed class NeverCompletingSource : IServiceHealthSnapshotSource
    {
        private readonly TaskCompletionSource<ServiceHealthSnapshot> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CallCount { get; private set; }

        internal CancellationToken ObservedToken { get; private set; }

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedToken = cancellationToken;
            return new ValueTask<ServiceHealthSnapshot>(completion.Task);
        }
    }

    private sealed class CancellationSource : IServiceHealthSnapshotSource
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken ObservedToken { get; private set; }

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadySnapshot;
        }
    }

    private sealed class AlternatingSource : IServiceHealthSnapshotSource
    {
        private int callCount;

        internal int CallCount => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref callCount);
            return ValueTask.FromResult(current % 2 == 0
                ? ReadySnapshot
                : new ServiceHealthSnapshot(
                    ServiceStartupPhase.BootstrapConfiguration,
                    ServiceMigrationReadinessState.Failed,
                    ServiceDatabaseReadinessState.Unreachable,
                    "migration.failed"));
        }
    }
}

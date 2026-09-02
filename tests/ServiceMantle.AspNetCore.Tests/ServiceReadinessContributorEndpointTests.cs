using System.Collections.Concurrent;
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

public sealed class ServiceReadinessContributorEndpointTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    [Fact]
    public async Task Base_not_ready_skips_contributors_and_preserves_the_existing_response()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Reachable,
            "installation.pending");
        var source = new FixedSource(snapshot);
        var contributor = new DelegateContributor(1, (_, _) =>
            ValueTask.FromResult(ServiceReadinessContributorResult.NotReady("business.rejected")));
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(source);
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });

        using var response = await application.GetTestClient().GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(0, contributor.CallCount);
        await AssertHealthResponseAsync(
            response,
            "not_ready",
            "pendingSetup",
            "notStarted",
            "reachable",
            "installation.pending");
    }

    [Fact]
    public async Task Ready_uses_one_snapshot_and_passes_the_same_instance_to_the_contributor()
    {
        var source = new FixedSource(ReadySnapshot);
        var contributor = new DelegateContributor(1, (snapshot, _) =>
            ValueTask.FromResult(ServiceReadinessContributorResult.NotReady("business.paused")));
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(source);
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });

        using var response = await application.GetTestClient().GetAsync(
            "/health",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, source.CallCount);
        Assert.Same(ReadySnapshot, Assert.Single(contributor.Snapshots));
        await AssertHealthResponseAsync(
            response,
            "not_ready",
            "completed",
            "succeeded",
            "reachable",
            "business.paused");
    }

    [Fact]
    public async Task Live_does_not_resolve_or_invoke_contributors_during_the_request()
    {
        var factoryCalls = 0;
        var contributor = new DelegateContributor(1, (_, _) =>
            throw new InvalidOperationException("connection-secret"));
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceReadinessContributor>(_ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return contributor;
            }));
        var factoryCallsAfterStartupValidation = factoryCalls;

        using var response = await application.GetTestClient().GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factoryCallsAfterStartupValidation);
        Assert.Equal(factoryCallsAfterStartupValidation, factoryCalls);
        Assert.Equal(0, contributor.CallCount);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("null")]
    public async Task Contributor_exception_and_null_result_fail_closed_without_internal_material(
        string scenario)
    {
        const string secret =
            "Host=db.internal;Port=5432;Username=admin;Password=token-secret;SELECT 1";
        var contributor = new DelegateContributor(1, (_, _) => scenario switch
        {
            "exception" => ValueTask.FromException<ServiceReadinessContributorResult>(
                new InvalidOperationException(secret)),
            "null" => ValueTask.FromResult<ServiceReadinessContributorResult>(null!),
            _ => throw new InvalidOperationException(),
        });
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });

        using var response = await application.GetTestClient().GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db.internal", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(DelegateContributor), body, StringComparison.Ordinal);
        await AssertHealthResponseAsync(
            response,
            "not_ready",
            "completed",
            "succeeded",
            "reachable",
            WellKnownServiceHealthErrorCodes.ContributorFailed);
    }

    [Fact]
    public async Task Contributor_total_budget_is_bounded_and_returns_the_timeout_code()
    {
        var contributor = new BlockingContributor();
        await using var application = await StartAsync(
            services =>
            {
                services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
                services.AddSingleton<IServiceReadinessContributor>(contributor);
            },
            options => options.ContributorTimeout =
                ServiceMantleHealthOptions.MinimumContributorTimeout);
        var started = DateTimeOffset.UtcNow;

        using var response = await application.GetTestClient().GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(contributor.ObservedToken.CanBeCanceled);
        Assert.True(contributor.ObservedToken.IsCancellationRequested);
        await AssertHealthResponseAsync(
            response,
            "not_ready",
            "completed",
            "succeeded",
            "reachable",
            WellKnownServiceHealthErrorCodes.ContributorTimeout);
    }

    [Fact]
    public async Task Request_cancellation_during_contribution_propagates_the_original_token()
    {
        var contributor = new BlockingContributor();
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });
        using var cancellation = new CancellationTokenSource();
        var request = application.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = "/health/ready";
            context.RequestAborted = cancellation.Token;
        }, TestContext.Current.CancellationToken);
        await contributor.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(contributor.ObservedToken.CanBeCanceled);
    }

    [Fact]
    public async Task Equivalent_generic_registration_is_idempotent()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-idempotent"))
            .AddServiceMantleHealthEndpoints()
            .AddServiceReadinessContributor<RegisteredContributor>()
            .AddServiceReadinessContributor<RegisteredContributor>();
        await using var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(application.Services.GetServices<IServiceReadinessContributor>());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("order")]
    public async Task Invalid_contributor_registration_fails_safely_when_the_host_starts(
        string scenario)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-invalid-contributor"))
            .AddServiceMantleHealthEndpoints();
        switch (scenario)
        {
            case "duplicate":
                builder.Services.AddSingleton<IServiceReadinessContributor>(
                    new DelegateContributor(1, Ready));
                builder.Services.AddSingleton<IServiceReadinessContributor>(
                    new AnotherContributorWithDuplicateOrder());
                break;
            case "null":
                builder.Services.AddSingleton<IServiceReadinessContributor>(_ => null!);
                break;
            case "order":
                builder.Services.AddSingleton<IServiceReadinessContributor>(
                    new SecretNamedThrowingOrderContributor());
                break;
        }
        await using var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            nameof(SecretNamedThrowingOrderContributor),
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(30_001)]
    public async Task Invalid_contributor_budget_fails_when_the_host_starts(int milliseconds)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-invalid-budget"))
            .AddServiceMantleHealthEndpoints(options =>
                options.ContributorTimeout = TimeSpan.FromMilliseconds(milliseconds));
        await using var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_requests_keep_snapshot_result_and_cancellation_state_isolated()
    {
        var source = new DistinctReadySource();
        var contributor = new SnapshotCodeContributor();
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(source);
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });
        var client = application.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await client.GetAsync(
                "/health/ready",
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            return (response.StatusCode, document.RootElement.GetProperty("errorCode").GetString());
        }));

        Assert.Equal(20, source.CallCount);
        Assert.Equal(20, contributor.Snapshots.Count);
        Assert.All(responses, item => Assert.Equal(HttpStatusCode.ServiceUnavailable, item.StatusCode));
        Assert.Equal(20, responses.Select(item => item.Item2).Distinct().Count());
        Assert.All(contributor.ObservedTokens, token => Assert.False(token.IsCancellationRequested));
    }

    private static async Task<WebApplication> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<ServiceMantleHealthOptions>? configureHealth = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-contributor"))
            .AddServiceMantleHealthEndpoints(configureHealth);
        configureServices?.Invoke(builder.Services);
        var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private static async Task AssertHealthResponseAsync(
        HttpResponseMessage response,
        string status,
        string? phase,
        string? migration,
        string? database,
        string? errorCode)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            ["status", "phase", "migrationStatus", "databaseStatus", "errorCode"],
            root.EnumerateObject().Select(item => item.Name));
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(phase, root.GetProperty("phase").GetString());
        Assert.Equal(migration, root.GetProperty("migrationStatus").GetString());
        Assert.Equal(database, root.GetProperty("databaseStatus").GetString());
        Assert.Equal(errorCode, root.GetProperty("errorCode").GetString());
    }

    private static ValueTask<ServiceReadinessContributorResult> Ready(
        ServiceHealthSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ServiceReadinessContributorResult.Ready());

    private sealed class FixedSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class DelegateContributor(
        int order,
        Func<ServiceHealthSnapshot, CancellationToken, ValueTask<ServiceReadinessContributorResult>> evaluate)
        : IServiceReadinessContributor
    {
        private int callCount;

        public int Order => order;

        public int CallCount => Volatile.Read(ref callCount);

        public ConcurrentBag<ServiceHealthSnapshot> Snapshots { get; } = [];

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            Snapshots.Add(snapshot);
            return evaluate(snapshot, cancellationToken);
        }
    }

    private sealed class BlockingContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public async ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ServiceReadinessContributorResult.Ready();
        }
    }

    private sealed class RegisteredContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceReadinessContributorResult.Ready());
    }

    private sealed class AnotherContributorWithDuplicateOrder : IServiceReadinessContributor
    {
        public int Order => 1;

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceReadinessContributorResult.Ready());
    }

    private sealed class SecretNamedThrowingOrderContributor : IServiceReadinessContributor
    {
        public int Order => throw new InvalidOperationException("connection-secret");

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DistinctReadySource : IServiceHealthSnapshotSource
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            return ValueTask.FromResult(new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable,
                $"request.{call}"));
        }
    }

    private sealed class SnapshotCodeContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public ConcurrentBag<ServiceHealthSnapshot> Snapshots { get; } = [];

        public ConcurrentBag<CancellationToken> ObservedTokens { get; } = [];

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            ObservedTokens.Add(cancellationToken);
            return ValueTask.FromResult(
                ServiceReadinessContributorResult.NotReady(snapshot.ErrorCode!));
        }
    }
}

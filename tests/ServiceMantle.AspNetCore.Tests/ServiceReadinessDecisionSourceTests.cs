using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the single readiness decision source shared by the ASP.NET Core health endpoints and by
/// optional packages that must not repeat the readiness algorithm.
/// </summary>
public sealed class ServiceReadinessDecisionSourceTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_source_is_registered_and_readable_without_an_http_request()
    {
        var source = new FixedSource(ReadySnapshot);
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.True(decision.IsReady);
        Assert.Same(ReadySnapshot, decision.Snapshot);
        Assert.Null(decision.ErrorCode);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task Endpoint_and_source_report_the_same_decision_for_the_same_state()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Reachable,
            "installation.pending");
        var contributor = new DelegateContributor(1, () =>
            ServiceReadinessContributorResult.NotReady("business.rejected"));
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(snapshot));
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);
        using var response = await application.GetTestClient().GetAsync("/health/ready", Token);

        Assert.False(decision.IsReady);
        Assert.Same(snapshot, decision.Snapshot);
        Assert.Equal("installation.pending", decision.ErrorCode);
        // The base snapshot is not ready, so contributors never run on either path.
        Assert.Equal(0, contributor.CallCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertResponseAsync(
            response,
            "not_ready",
            "pendingSetup",
            "notStarted",
            "reachable",
            "installation.pending");
    }

    [Fact]
    public async Task Contributor_rejection_keeps_the_snapshot_and_uses_the_contributor_code()
    {
        var contributor = new DelegateContributor(1, () =>
            ServiceReadinessContributorResult.NotReady("business.paused"));
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.False(decision.IsReady);
        Assert.Same(ReadySnapshot, decision.Snapshot);
        Assert.Equal("business.paused", decision.ErrorCode);
        Assert.Same(ReadySnapshot, Assert.Single(contributor.Snapshots));
    }

    [Fact]
    public async Task Contributor_failure_and_budget_exhaustion_stay_bounded()
    {
        await using var failing = await StartAsync(
            services =>
            {
                services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
                services.AddSingleton<IServiceReadinessContributor>(new ThrowingContributor());
            });
        await using var blocked = await StartAsync(
            services =>
            {
                services.AddSingleton<IServiceHealthSnapshotSource>(new FixedSource(ReadySnapshot));
                services.AddSingleton<IServiceReadinessContributor>(new BlockingContributor());
            },
            options => options.ContributorTimeout =
                HealthOptions.MinimumContributorTimeout);
        using var failingScope = failing.Services.CreateScope();
        using var blockedScope = blocked.Services.CreateScope();

        var failed = await failingScope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);
        var timedOut = await blockedScope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.Same(ReadySnapshot, failed.Snapshot);
        Assert.Equal(WellKnownServiceHealthErrorCodes.ContributorFailed, failed.ErrorCode);
        Assert.Same(ReadySnapshot, timedOut.Snapshot);
        Assert.Equal(WellKnownServiceHealthErrorCodes.ContributorTimeout, timedOut.ErrorCode);
        Assert.DoesNotContain("probe-secret", failed.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("throws")]
    public async Task Snapshot_failures_fail_closed_without_a_snapshot(string mode)
    {
        await using var application = await StartAsync(services =>
        {
            if (mode != "missing")
            {
                services.AddSingleton<IServiceHealthSnapshotSource>(new BrokenSource(mode));
            }
        });
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.False(decision.IsReady);
        Assert.Null(decision.Snapshot);
        Assert.Equal(WellKnownServiceHealthErrorCodes.ProbeFailed, decision.ErrorCode);
        Assert.DoesNotContain("probe-secret", decision.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Internal_probe_timeout_stays_a_timeout_without_a_snapshot()
    {
        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(new BlockingSource()),
            options => options.ProbeTimeout = HealthOptions.MinimumProbeTimeout);
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.False(decision.IsReady);
        Assert.Null(decision.Snapshot);
        Assert.Equal(WellKnownServiceHealthErrorCodes.ProbeTimeout, decision.ErrorCode);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_the_original_token_to_the_source()
    {
        var source = new BlockingSource();
        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();
        using var abort = new CancellationTokenSource();
        var decision = scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(abort.Token)
            .AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        abort.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decision);

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.True(source.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Already_cancelled_calls_never_resolve_the_snapshot_source()
    {
        var resolutions = 0;
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return new FixedSource(ReadySnapshot);
            }));
        using var scope = application.Services.CreateScope();
        using var abort = new CancellationTokenSource();
        abort.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scope.ServiceProvider
                .GetRequiredService<IServiceReadinessDecisionSource>()
                .GetDecisionAsync(abort.Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref resolutions));
    }

    [Fact]
    public async Task Concurrent_decisions_are_isolated_and_sample_once_each()
    {
        var source = new DistinctReadySource();
        var contributor = new SnapshotCodeContributor();
        await using var application = await StartAsync(services =>
        {
            services.AddSingleton<IServiceHealthSnapshotSource>(source);
            services.AddSingleton<IServiceReadinessContributor>(contributor);
        });

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var scope = application.Services.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<IServiceReadinessDecisionSource>()
                .GetDecisionAsync(Token);
        }));

        Assert.Equal(20, source.CallCount);
        Assert.Equal(20, decisions.Select(decision => decision.ErrorCode).Distinct().Count());
        Assert.All(decisions, decision =>
        {
            Assert.False(decision.IsReady);
            Assert.NotNull(decision.Snapshot);
            Assert.Equal(decision.Snapshot!.ErrorCode, decision.ErrorCode);
        });
        Assert.Equal(20, contributor.Snapshots.Distinct().Count());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null-decision")]
    [InlineData("throws")]
    [InlineData("forged-ready")]
    public async Task Endpoint_fails_closed_when_a_replaced_decision_source_misbehaves(string mode)
    {
        await using var application = await StartAsync(configureBefore: services =>
            services.AddScoped<IServiceReadinessDecisionSource>(_ => mode == "missing"
                ? null!
                : new BrokenDecisionSource(mode)));

        using var response = await application.GetTestClient().GetAsync("/health/ready", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertResponseAsync(
            response,
            "not_ready",
            phase: null,
            migration: null,
            database: null,
            WellKnownServiceHealthErrorCodes.ProbeFailed);
    }

    [Fact]
    public async Task Replaced_decision_source_keeps_the_registered_implementation()
    {
        var replacement = new FixedDecisionSource(ServiceReadinessDecision.NotReady(
            new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Unreachable),
            "database.unreachable"));
        await using var application = await StartAsync(configureBefore: services =>
            services.AddScoped<IServiceReadinessDecisionSource>(_ => replacement));
        using var scope = application.Services.CreateScope();

        Assert.Same(
            replacement,
            scope.ServiceProvider.GetRequiredService<IServiceReadinessDecisionSource>());
        using var response = await application.GetTestClient().GetAsync("/health/ready", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertResponseAsync(
            response,
            "not_ready",
            "completed",
            "succeeded",
            "unreachable",
            "database.unreachable");
    }

    [Fact]
    public async Task Unavailable_decisions_project_null_state_fields()
    {
        var replacement = new FixedDecisionSource(
            ServiceReadinessDecision.Unavailable(WellKnownServiceHealthErrorCodes.ProbeTimeout));
        await using var application = await StartAsync(configureBefore: services =>
            services.AddScoped<IServiceReadinessDecisionSource>(_ => replacement));

        using var response = await application.GetTestClient().GetAsync("/health", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertResponseAsync(
            response,
            "not_ready",
            phase: null,
            migration: null,
            database: null,
            WellKnownServiceHealthErrorCodes.ProbeTimeout);
    }

    [Fact]
    public async Task Live_never_resolves_the_decision_source()
    {
        var resolutions = 0;
        await using var application = await StartAsync(configureBefore: services =>
            services.AddScoped<IServiceReadinessDecisionSource>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return new FixedDecisionSource(ServiceReadinessDecision.Ready(ReadySnapshot));
            }));

        using var live = await application.GetTestClient().GetAsync("/health/live", Token);
        using var ready = await application.GetTestClient().GetAsync("/health/ready", Token);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(1, Volatile.Read(ref resolutions));
    }

    private static async Task<WebApplication> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<HealthOptions>? configureHealth = null,
        Action<IServiceCollection>? configureBefore = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        configureBefore?.Invoke(builder.Services);
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health"), InstanceId.Parse("health-decision"))
            .AddServiceMantleHealthEndpoints(configureHealth);
        configureServices?.Invoke(builder.Services);
        var application = builder.Build();
        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(Token);
        return application;
    }

    private static async Task AssertResponseAsync(
        HttpResponseMessage response,
        string status,
        string? phase,
        string? migration,
        string? database,
        string? errorCode)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(Token));
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

    private sealed class BrokenSource(string mode) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => mode switch
            {
                "null" => ValueTask.FromResult<ServiceHealthSnapshot>(null!),
                _ => throw new InvalidOperationException("probe-secret"),
            };
    }

    private sealed class BlockingSource : IServiceHealthSnapshotSource
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadySnapshot;
        }
    }

    private sealed class DelegateContributor(
        int order,
        Func<ServiceReadinessContributorResult> evaluate) : IServiceReadinessContributor
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
            return ValueTask.FromResult(evaluate());
        }
    }

    private sealed class ThrowingContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe-secret");
    }

    private sealed class BlockingContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public async ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ServiceReadinessContributorResult.Ready();
        }
    }

    private sealed class SnapshotCodeContributor : IServiceReadinessContributor
    {
        public int Order => 1;

        public ConcurrentBag<ServiceHealthSnapshot> Snapshots { get; } = [];

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return ValueTask.FromResult(
                ServiceReadinessContributorResult.NotReady(snapshot.ErrorCode!));
        }
    }

    private sealed class FixedDecisionSource(ServiceReadinessDecision decision)
        : IServiceReadinessDecisionSource
    {
        public ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class BrokenDecisionSource(string mode) : IServiceReadinessDecisionSource
    {
        public ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default) => mode switch
            {
                "null-decision" => ValueTask.FromResult<ServiceReadinessDecision>(null!),
                // A replacement source cannot promote a base-unready snapshot: the core factory
                // rejects it and the endpoint fails closed on the resulting failure.
                "forged-ready" => ValueTask.FromResult(ServiceReadinessDecision.Ready(
                    new ServiceHealthSnapshot(
                        ServiceStartupPhase.BootstrapConfiguration,
                        ServiceMigrationReadinessState.Succeeded,
                        ServiceDatabaseReadinessState.Reachable))),
                _ => throw new InvalidOperationException("probe-secret"),
            };
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers one invariant of the readiness read: at this layer's final output checkpoint, a caller
/// cancellation that has already been requested outranks an ordinary failure, a
/// <see cref="TimeoutException"/>, and an ordinary decision alike. It is asserted on both public
/// exits of the same pipeline - the default decision source, and the readiness endpoints over any
/// registered decision source.
/// </summary>
/// <remarks>
/// The guarantee stops at that checkpoint. A cancellation arriving between it and the caller
/// receiving the result is not covered, neither is the delay between a transport-level abort and
/// the server's request token, and a third party that blocks or throws from a cancellation callback
/// is neither interrupted nor cleaned up here. An internal budget timeout without a caller
/// cancellation is still a timeout.
/// </remarks>
public sealed class ServiceReadinessCancellationPriorityTests
{
    private const string SyntheticProbeSecret = "synthetic-readiness-probe-secret";

    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> SnapshotOutcomes() =>
    [
        "failure",
        "timeout-exception",
        "internal-cancellation",
        "snapshot",
        "null-snapshot"
    ];

    public static TheoryData<string, string> ReadinessRoutesAndDecisions()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { "/health/ready", "/health" })
        {
            foreach (var decision in new[]
                     {
                         "ready", "not-ready", "unavailable", "null",
                         "failure", "timeout-exception", "internal-cancellation"
                     })
            {
                data.Add(path, decision);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SnapshotOutcomes))]
    public async Task The_default_source_reports_the_caller_s_cancellation_over_its_own_outcome(
        string outcome)
    {
        using var abort = new CancellationTokenSource();
        var source = new CancellingSnapshotSource(abort, outcome);
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scope.ServiceProvider
                .GetRequiredService<IServiceReadinessDecisionSource>()
                .GetDecisionAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.DoesNotContain(SyntheticProbeSecret, failure.ToString(), StringComparison.Ordinal);
        // The token this read owns was notified on the way out, on the token the source received.
        Assert.True(source.ObservedToken.IsCancellationRequested);
        // One read per decision, and the fix added none.
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData("failure", WellKnownServiceHealthErrorCodes.ProbeFailed)]
    [InlineData("timeout-exception", WellKnownServiceHealthErrorCodes.ProbeTimeout)]
    [InlineData("internal-cancellation", WellKnownServiceHealthErrorCodes.ProbeFailed)]
    [InlineData("null-snapshot", WellKnownServiceHealthErrorCodes.ProbeFailed)]
    public async Task An_uncancelled_snapshot_failure_keeps_its_finite_classification(
        string outcome,
        string expectedErrorCode)
    {
        var source = new CancellingSnapshotSource(abort: null, outcome);
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.False(decision.IsReady);
        Assert.Null(decision.Snapshot);
        Assert.Equal(expectedErrorCode, decision.ErrorCode);
    }

    [Fact]
    public async Task An_uncancelled_ready_snapshot_still_reports_ready()
    {
        var source = new CancellingSnapshotSource(abort: null, "snapshot");
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();

        var decision = await scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>()
            .GetDecisionAsync(Token);

        Assert.True(decision.IsReady);
        Assert.Same(ReadySnapshot, decision.Snapshot);
    }

    [Fact]
    public async Task An_internal_budget_timeout_without_caller_cancellation_stays_a_timeout()
    {
        var source = new BlockingSnapshotSource();
        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            options => options.ProbeTimeout = HealthOptions.MinimumProbeTimeout);
        using var scope = application.Services.CreateScope();
        try
        {
            var decision = await scope.ServiceProvider
                .GetRequiredService<IServiceReadinessDecisionSource>()
                .GetDecisionAsync(Token);

            Assert.False(decision.IsReady);
            Assert.Equal(WellKnownServiceHealthErrorCodes.ProbeTimeout, decision.ErrorCode);
            Assert.True(source.ObservedToken.IsCancellationRequested);
        }
        finally
        {
            // The fixture owns releasing and awaiting the probe it blocked.
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Two_overlapping_reads_do_not_share_a_token_or_a_decision()
    {
        using var abort = new CancellationTokenSource();
        using var unaffected = new CancellationTokenSource();
        var source = new OverlappingSnapshotSource(abort);
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceHealthSnapshotSource>(source));
        using var scope = application.Services.CreateScope();
        var decisionSource = scope.ServiceProvider
            .GetRequiredService<IServiceReadinessDecisionSource>();

        // The first read stays in flight until the second one has started, and only then cancels
        // its own caller.
        var cancelledRead = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await decisionSource.GetDecisionAsync(abort.Token));
        var completingRead = await decisionSource
            .GetDecisionAsync(unaffected.Token)
            .AsTask()
            .WaitAsync(Observation, Token);

        var failure = await cancelledRead.WaitAsync(Observation, Token);
        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.True(completingRead.IsReady);
        Assert.False(unaffected.IsCancellationRequested);
        // Each read carried its own linked token into the source.
        Assert.NotEqual(source.FirstToken, source.SecondToken);
        Assert.False(source.SecondToken.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(ReadinessRoutesAndDecisions))]
    public async Task A_readiness_request_cancelled_by_its_decision_source_ends_with_its_own_token(
        string path,
        string decision)
    {
        using var abort = new CancellationTokenSource();
        var decisionSource = new CancellingDecisionSource(abort, decision);
        var observation = new HandlerObservation();
        await using var application = await StartAsync(
            services => services.AddSingleton<IServiceReadinessDecisionSource>(decisionSource),
            observe: observation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.RequestAborted = abort.Token;
                },
                Token));

        // The handler's own result, not the client's: an HttpClient that cancels itself would prove
        // nothing about what this endpoint did.
        var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
        Assert.True(failure.CarriesRequestToken);
        Assert.Equal(abort.Token, failure.Error.CancellationToken);
        Assert.DoesNotContain(SyntheticProbeSecret, failure.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, decisionSource.Calls);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task An_uncancelled_readiness_request_still_projects_its_decision(string path)
    {
        var decisionSource = new CancellingDecisionSource(abort: null, "ready");
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceReadinessDecisionSource>(decisionSource));

        using var response = await application.GetTestClient().GetAsync(path, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task An_already_cancelled_readiness_request_never_resolves_a_decision_source(
        string path)
    {
        var resolutions = 0;
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var decisionSource = new CancellingDecisionSource(abort: null, "ready");
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceReadinessDecisionSource>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return decisionSource;
            }));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.RequestAborted = abort.Token;
                },
                Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref resolutions));
        Assert.Equal(0, decisionSource.Calls);
    }

    [Fact]
    public async Task The_live_route_still_resolves_no_readiness_decision_source()
    {
        var resolutions = 0;
        var decisionSource = new CancellingDecisionSource(abort: null, "ready");
        await using var application = await StartAsync(services =>
            services.AddSingleton<IServiceReadinessDecisionSource>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return decisionSource;
            }));

        using var response = await application.GetTestClient().GetAsync("/health/live", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref resolutions));
        Assert.Equal(0, decisionSource.Calls);
    }

    private static async Task<WebApplication> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<HealthOptions>? configureHealth = null,
        HandlerObservation? observe = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("health-priority"),
                InstanceId.Parse("health-priority-01"))
            .AddServiceMantleHealthEndpoints(configureHealth);
        configureServices?.Invoke(builder.Services);
        var application = builder.Build();
        if (observe is not null)
        {
            application.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (OperationCanceledException error)
                {
                    observe.Failure.TrySetResult(
                        new HandlerFailure(error, error.CancellationToken == context.RequestAborted));
                    throw;
                }
            });
        }

        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(Token);
        return application;
    }

    private sealed record HandlerFailure(OperationCanceledException Error, bool CarriesRequestToken);

    private sealed class HandlerObservation
    {
        internal TaskCompletionSource<HandlerFailure> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A snapshot source that requests the caller's cancellation, when the test gave it one, and
    /// then finishes the way the test asked - normally, with an ordinary failure, with a timeout, or
    /// with somebody else's cancellation.
    /// </summary>
    private sealed class CancellingSnapshotSource(CancellationTokenSource? abort, string outcome)
        : IServiceHealthSnapshotSource
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal CancellationToken ObservedToken { get; private set; }

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            ObservedToken = cancellationToken;
            if (abort is not null)
            {
                await abort.CancelAsync();
            }

            return outcome switch
            {
                "snapshot" => ReadySnapshot,
                "null-snapshot" => null!,
                "timeout-exception" => throw new TimeoutException(SyntheticProbeSecret),
                "internal-cancellation" => throw new OperationCanceledException(
                    SyntheticProbeSecret,
                    new CancellationToken(canceled: true)),
                _ => throw new InvalidOperationException(SyntheticProbeSecret),
            };
        }
    }

    /// <summary>A source that blocks until the fixture releases it, honouring the token it got.</summary>
    private sealed class BlockingSnapshotSource : IServiceHealthSnapshotSource
    {
        private readonly TaskCompletionSource<ServiceHealthSnapshot> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task<ServiceHealthSnapshot> read = Task.FromResult(ReadySnapshot);

        internal CancellationToken ObservedToken { get; private set; }

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            read = release.Task.WaitAsync(cancellationToken);
            return new ValueTask<ServiceHealthSnapshot>(read);
        }

        internal async Task ReleaseAsync()
        {
            release.TrySetResult(ReadySnapshot);
            try
            {
                await read.WaitAsync(Observation, Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Keeps the first read in flight until the second one has started, then cancels only the first
    /// read's own caller. Both reads still return an ordinary snapshot.
    /// </summary>
    private sealed class OverlappingSnapshotSource(CancellationTokenSource abort)
        : IServiceHealthSnapshotSource
    {
        private readonly TaskCompletionSource secondStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int calls;

        internal CancellationToken FirstToken { get; private set; }

        internal CancellationToken SecondToken { get; private set; }

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstToken = cancellationToken;
                await secondStarted.Task;
                await abort.CancelAsync();
                return ReadySnapshot;
            }

            SecondToken = cancellationToken;
            secondStarted.TrySetResult();
            return ReadySnapshot;
        }
    }

    /// <summary>
    /// A replaced decision source: the endpoint's cancellation contract has to hold over any
    /// registered source, not only the default one.
    /// </summary>
    private sealed class CancellingDecisionSource(CancellationTokenSource? abort, string decision)
        : IServiceReadinessDecisionSource
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        public async ValueTask<ServiceReadinessDecision> GetDecisionAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            if (abort is not null)
            {
                await abort.CancelAsync();
            }

            return decision switch
            {
                "ready" => ServiceReadinessDecision.Ready(ReadySnapshot),
                "not-ready" => ServiceReadinessDecision.NotReady(ReadySnapshot, "business.rejected"),
                "unavailable" => ServiceReadinessDecision.Unavailable(
                    WellKnownServiceHealthErrorCodes.ProbeFailed),
                "null" => null!,
                "timeout-exception" => throw new TimeoutException(SyntheticProbeSecret),
                "internal-cancellation" => throw new OperationCanceledException(
                    SyntheticProbeSecret,
                    new CancellationToken(canceled: true)),
                _ => throw new InvalidOperationException(SyntheticProbeSecret),
            };
        }
    }
}

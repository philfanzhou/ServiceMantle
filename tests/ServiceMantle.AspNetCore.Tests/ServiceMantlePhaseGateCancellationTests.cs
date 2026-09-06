using System.Collections.Concurrent;
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
/// Covers the phase gate's cancellation contract: a cooperative snapshot source is notified on the
/// token it received before the gate leaves its cancellation exit, the caller still observes its own
/// <see cref="OperationCanceledException"/>, and the endpoint never runs.
/// </summary>
public sealed class ServiceMantlePhaseGateCancellationTests
{
    private static readonly ServiceHealthSnapshot Ready = new(ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/management/bootstrap")]
    [InlineData("/management/setup")]
    [InlineData("/management/settings")]
    [InlineData("/business")]
    public async Task Caller_cancellation_notifies_the_cooperative_source_before_the_gate_exits(string path)
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        var observation = new HandlerObservation();
        await using var app = await StartAsync(source, executions, observation);
        using var abort = new CancellationTokenSource();
        try
        {
            var request = app.GetTestServer().SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = path;
                context.RequestAborted = abort.Token;
            }, Token);
            var call = await source.WaitForCallAsync(0);
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            // The caller's result, the gate's cancellation and the source's own token are asserted
            // separately; the first two cannot stand in for the third.
            Assert.True(failure.CarriesRequestToken);
            // Asserted without waiting: the gate owns the linked cancellation, so the source has
            // already been notified once the gate leaves. Waiting would accept the lost notification.
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Read.WaitAsync(Observation, Token));
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, executions.Count);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Theory]
    [InlineData("/business")]
    [InlineData("/management/settings")]
    public async Task Caller_cancellation_over_real_http_notifies_the_cooperative_source(string path)
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        var observation = new HandlerObservation();
        using var serverAbort = new CancellationTokenSource();
        await using var app = await StartAsync(source, executions, observation, kestrel: true, serverAbort: serverAbort);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var clientAbort = new CancellationTokenSource();
        try
        {
            var request = client.GetAsync(path, clientAbort.Token);
            var call = await source.WaitForCallAsync(0);
            clientAbort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            // The client's transport abort and the server's request token are observed separately:
            // FIN/RST notification timing is not part of this guarantee.
            serverAbort.Cancel();
            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            Assert.True(failure.CarriesRequestToken);
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Read.WaitAsync(Observation, Token));
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, executions.Count);
        }
        finally
        {
            await source.ReleaseAsync();
            await app.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("/management/bootstrap")]
    [InlineData("/management/setup")]
    [InlineData("/management/settings")]
    [InlineData("/business")]
    public async Task Already_cancelled_request_never_reads_the_source_or_runs_the_endpoint(string path)
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        await using var app = await StartAsync(source, executions);
        using var abort = new CancellationTokenSource();
        abort.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.RequestAborted = abort.Token;
        }, Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, executions.Count);
    }

    [Fact]
    public async Task Snapshot_completion_meeting_cancellation_remains_cancellation()
    {
        using var abort = new CancellationTokenSource();
        var source = new CompletingSource(abort);
        var executions = new Executions();
        var observation = new HandlerObservation();
        await using var app = await StartAsync(source, executions, observation);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = "/business";
            context.RequestAborted = abort.Token;
        }, Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, executions.Count);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_share_cancellation_and_still_sample_once()
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        var observation = new HandlerObservation();
        await using var app = await StartAsync(source, executions, observation);
        using var cancelled = new CancellationTokenSource();
        using var admitted = new CancellationTokenSource();
        try
        {
            var cancelledRequest = app.GetTestServer().SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/business";
                context.RequestAborted = cancelled.Token;
            }, Token);
            var first = await source.WaitForCallAsync(0);
            var admittedRequest = app.GetTestServer().SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/management/settings";
                context.RequestAborted = admitted.Token;
            }, Token);
            var second = await source.WaitForCallAsync(1);

            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            Assert.Equal(cancelled.Token, failure.Error.CancellationToken);
            Assert.True(first.CancellationObserved.Task.IsCompleted);
            Assert.True(first.ObservedToken.IsCancellationRequested);
            Assert.False(second.CancellationObserved.Task.IsCompleted);
            Assert.False(second.ObservedToken.IsCancellationRequested);
            Assert.NotEqual(first.ObservedToken, second.ObservedToken);

            source.Release();
            var context = await admittedRequest.WaitAsync(Observation, Token);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.False(second.ObservedToken.IsCancellationRequested);
            Assert.Equal(2, source.Calls);
            Assert.Equal(1, executions.Count);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Theory]
    [InlineData("/management/status", HttpStatusCode.OK)]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/not-mapped", HttpStatusCode.NotFound)]
    public async Task Status_live_and_unmatched_routes_never_read_the_source(string path, HttpStatusCode expected)
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        await using var app = await StartAsync(source, executions);

        using var response = await app.GetTestClient().GetAsync(path, Token);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Internal_timeout_notifies_the_source_and_still_rejects()
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        await using var app = await StartAsync(source, executions,
            configure: options => options.SnapshotTimeout = TimeSpan.FromMilliseconds(50));
        try
        {
            using var response = await app.GetTestClient().GetAsync("/business", Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", await response.Content.ReadAsStringAsync(Token));
            Assert.True(response.Headers.CacheControl?.NoStore);
            var call = await source.WaitForCallAsync(0);
            await call.CancellationObserved.Task.WaitAsync(Observation, Token);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            Assert.Equal(0, executions.Count);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    private static async Task<WebApplication> StartAsync(IServiceHealthSnapshotSource source, Executions executions,
        HandlerObservation? observation = null, Action<ServiceMantlePhaseGateOptions>? configure = null,
        bool kestrel = false, CancellationTokenSource? serverAbort = null)
    {
        var builder = kestrel ? WebApplication.CreateSlimBuilder() : WebApplication.CreateBuilder();
        if (kestrel) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var service = builder.Services.AddServiceMantle(ServiceId.Parse("gate"), InstanceId.Parse("gate-01"));
        service.AddServiceMantlePhaseGate(configure);
        service.AddServiceMantleHealthEndpoints();
        builder.Services.AddSingleton(source);
        var app = builder.Build();
        app.UseRouting();
        if (serverAbort is not null)
        {
            // Bind an explicit server-side abort so the request token is observed independently of
            // the client's transport abort.
            app.Use(async (context, next) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, serverAbort.Token);
                context.RequestAborted = linked.Token;
                await next(context);
            });
        }
        if (observation is not null)
        {
            // Observes the gate's own cancellation result, which the caller's result cannot stand in for.
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (OperationCanceledException error)
                {
                    observation.Failure.TrySetResult(new HandlerFailure(error, error.CancellationToken == context.RequestAborted));
                    throw;
                }
            });
        }
        app.UseServiceMantlePhaseGate();
        var management = app.MapServiceMantleManagementGroup();
        management.MapGet("/status", () => executions.Run("status")).WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Status);
        management.MapGet("/bootstrap", () => executions.Run("bootstrap")).WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Bootstrap);
        management.MapGet("/setup", () => executions.Run("setup")).WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Setup);
        management.MapGet("/settings", () => executions.Run("settings")).WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management);
        app.MapServiceMantleHealthEndpoints();
        app.MapGet("/business", () => executions.Run("business"));
        await app.StartAsync(Token);
        return app;
    }

    private sealed class Executions
    {
        private int count;
        internal int Count => Volatile.Read(ref count);
        internal string Run(string result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
    }

    private sealed record HandlerFailure(OperationCanceledException Error, bool CarriesRequestToken);

    private sealed class HandlerObservation
    {
        internal TaskCompletionSource<HandlerFailure> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SourceCall(CancellationToken observedToken)
    {
        internal CancellationToken ObservedToken { get; } = observedToken;
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration { get; set; }
        internal Task<ServiceHealthSnapshot> Read { get; set; } =
            Task.FromException<ServiceHealthSnapshot>(new InvalidOperationException("The read was not started."));
    }

    /// <summary>
    /// A cooperative source: it honours the token it receives, its cancellation callback is bounded
    /// and does not throw, and the fixture owns releasing and awaiting its own reads.
    /// </summary>
    private sealed class CooperativeSource : IServiceHealthSnapshotSource
    {
        private readonly TaskCompletionSource<ServiceHealthSnapshot> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, SourceCall> calls = new();
        private int callCount;
        internal int Calls => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref callCount) - 1;
            var call = new SourceCall(cancellationToken);
            // The registration outlives the read on purpose: disposing it from the read itself would
            // race the token's own callback list and could drop the notification under observation.
            call.Registration = cancellationToken.Register(() => call.CancellationObserved.TrySetResult());
            call.Read = release.Task.WaitAsync(cancellationToken);
            calls[index] = call;
            return new ValueTask<ServiceHealthSnapshot>(call.Read);
        }

        /// <summary>
        /// Returns the recorded call once the source has received the token and registered its
        /// cancellation callback; both happen before the read task is published.
        /// </summary>
        internal async Task<SourceCall> WaitForCallAsync(int index)
        {
            var deadline = DateTimeOffset.UtcNow + Observation;
            while (!calls.ContainsKey(index))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The snapshot source was not entered.");
                await Task.Delay(10, Token);
            }
            return calls[index];
        }

        internal void Release() => release.TrySetResult(Ready);

        internal async Task ReleaseAsync()
        {
            Release();
            foreach (var call in calls.Values)
            {
                try
                {
                    await call.Read.WaitAsync(Observation, Token);
                }
                catch (OperationCanceledException)
                {
                }
                await call.Registration.DisposeAsync();
            }
        }
    }

    private sealed class CompletingSource(CancellationTokenSource abort) : IServiceHealthSnapshotSource
    {
        private int callCount;
        internal int Calls => Volatile.Read(ref callCount);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            // The snapshot is already available when the caller's cancellation arrives.
            abort.Cancel();
            return ValueTask.FromResult(Ready);
        }
    }
}

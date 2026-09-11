using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
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
/// Covers the readiness cancellation contract: a cooperative snapshot source is notified on the
/// token it received before the handler leaves its cancellation exit, and the caller still observes
/// its own <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class HealthCancellationTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Caller_cancellation_notifies_the_cooperative_source_before_the_handler_exits(string path)
    {
        var source = new CooperativeSource();
        var observation = new HandlerObservation();
        await using var application = await StartTestServerAsync(source, observation: observation);
        using var abort = new CancellationTokenSource();
        try
        {
            var request = application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.RequestAborted = abort.Token;
                },
                Token);
            var call = await source.WaitForCallAsync(0);

            abort.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            // The three observations are asserted separately: the caller's result, the handler's
            // cancellation, and the token the source itself received.
            Assert.True(failure.CarriesRequestToken);
            // Asserted without waiting: the handler owns the linked cancellation, so the source has
            // already been notified once the handler leaves. Waiting here would accept the lost
            // notification this fixes.
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => call.Read.WaitAsync(Observation, Token));
            Assert.Equal(1, source.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Caller_cancellation_over_real_http_notifies_the_cooperative_source(string path)
    {
        var source = new CooperativeSource();
        var observation = new HandlerObservation();
        using var serverAbort = new CancellationTokenSource();
        await using var application = await StartKestrelAsync(source, observation, serverAbort);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(application.Urls)) };
        using var clientAbort = new CancellationTokenSource();
        try
        {
            var request = client.GetAsync(path, clientAbort.Token);
            var call = await source.WaitForCallAsync(0);

            clientAbort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            // The caller's transport abort and the server's request token are observed separately:
            // FIN/RST notification timing is not part of this guarantee.
            serverAbort.Cancel();

            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            Assert.True(failure.CarriesRequestToken);
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => call.Read.WaitAsync(Observation, Token));
            Assert.Equal(1, source.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
            await application.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Already_cancelled_request_never_resolves_the_source(string path)
    {
        var resolutions = 0;
        var source = new CooperativeSource();
        await using var application = await StartTestServerAsync(source, () => Interlocked.Increment(ref resolutions));
        using var abort = new CancellationTokenSource();
        abort.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.RequestAborted = abort.Token;
                },
                Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref resolutions));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Snapshot_completion_meeting_cancellation_remains_cancellation(string path)
    {
        using var abort = new CancellationTokenSource();
        var source = new CompletingSource(abort);
        await using var application = await StartTestServerAsync(source);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.RequestAborted = abort.Token;
                },
                Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_share_cancellation_and_still_sample_once()
    {
        var source = new CooperativeSource();
        var observation = new HandlerObservation();
        await using var application = await StartTestServerAsync(source, observation: observation);
        using var cancelled = new CancellationTokenSource();
        using var completing = new CancellationTokenSource();
        try
        {
            var cancelledRequest = application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = "/health/ready";
                    context.RequestAborted = cancelled.Token;
                },
                Token);
            var first = await source.WaitForCallAsync(0);
            var completingRequest = application.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = "/health";
                    context.RequestAborted = completing.Token;
                },
                Token);
            var second = await source.WaitForCallAsync(1);

            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
            var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
            Assert.True(failure.CarriesRequestToken);
            Assert.Equal(cancelled.Token, failure.Error.CancellationToken);
            Assert.True(first.CancellationObserved.Task.IsCompleted);
            Assert.True(first.ObservedToken.IsCancellationRequested);
            Assert.False(second.CancellationObserved.Task.IsCompleted);
            Assert.False(second.ObservedToken.IsCancellationRequested);
            Assert.NotEqual(first.ObservedToken, second.ObservedToken);

            source.Release();
            var context = await completingRequest.WaitAsync(Observation, Token);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.False(second.ObservedToken.IsCancellationRequested);
            Assert.Equal(2, source.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Internal_timeout_notifies_the_source_and_keeps_the_timeout_classification()
    {
        var source = new CooperativeSource();
        await using var application = await StartTestServerAsync(
            source,
            configure: options => options.ProbeTimeout = HealthOptions.MinimumProbeTimeout);
        try
        {
            using var response = await application.GetTestClient().GetAsync("/health/ready", Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                WellKnownServiceHealthErrorCodes.ProbeTimeout,
                await ReadErrorCodeAsync(response));
            var call = await source.WaitForCallAsync(0);
            await call.CancellationObserved.Task.WaitAsync(Observation, Token);
            Assert.True(call.ObservedToken.IsCancellationRequested);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Source_cancellation_without_caller_cancellation_stays_probe_failed()
    {
        await using var application = await StartTestServerAsync(new SelfCancellingSource());

        using var response = await application.GetTestClient().GetAsync("/health/ready", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            WellKnownServiceHealthErrorCodes.ProbeFailed,
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("probe-secret", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("errorCode").GetString();
    }

    private static async Task<WebApplication> StartTestServerAsync(
        IServiceHealthSnapshotSource source,
        Action? onResolved = null,
        Action<HealthOptions>? configure = null,
        HandlerObservation? observation = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health-cancellation"), InstanceId.Parse("health-cancellation-01"))
            .AddServiceMantleHealthEndpoints(configure);
        builder.Services.AddSingleton(_ =>
        {
            onResolved?.Invoke();
            return source;
        });
        var application = builder.Build();
        if (observation is not null)
        {
            Observe(application, observation);
        }

        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(Token);
        return application;
    }

    private static async Task<WebApplication> StartKestrelAsync(
        IServiceHealthSnapshotSource source,
        HandlerObservation observation,
        CancellationTokenSource serverAbort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("health-cancellation"), InstanceId.Parse("health-cancellation-01"))
            .AddServiceMantleHealthEndpoints();
        builder.Services.AddSingleton(source);
        var application = builder.Build();
        // Bind an explicit server-side abort so the request token is observed independently of the
        // client's transport abort.
        application.Use(async (context, next) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                serverAbort.Token);
            context.RequestAborted = linked.Token;
            await next(context);
        });
        Observe(application, observation);
        application.MapServiceMantleHealthEndpoints();
        await application.StartAsync(Token);
        return application;
    }

    /// <summary>
    /// Observes the handler's own cancellation result, which the client's result cannot stand in for.
    /// </summary>
    private static void Observe(WebApplication application, HandlerObservation observation) =>
        application.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (OperationCanceledException error)
            {
                observation.Failure.TrySetResult(
                    new HandlerFailure(error, error.CancellationToken == context.RequestAborted));
                throw;
            }
        });

    private sealed record HandlerFailure(OperationCanceledException Error, bool CarriesRequestToken);

    private sealed class HandlerObservation
    {
        internal TaskCompletionSource<HandlerFailure> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SourceCall(CancellationToken observedToken)
    {
        internal CancellationToken ObservedToken { get; } = observedToken;

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenRegistration Registration { get; set; }

        internal Task<ServiceHealthSnapshot> Read { get; set; } = Task.FromException<ServiceHealthSnapshot>(
            new InvalidOperationException("The read was not started."));
    }

    /// <summary>
    /// A cooperative source: it honours the token it receives, its cancellation callback is bounded
    /// and does not throw, and the fixture owns releasing and awaiting its own reads.
    /// </summary>
    private sealed class CooperativeSource : IServiceHealthSnapshotSource
    {
        private readonly TaskCompletionSource<ServiceHealthSnapshot> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            var read = release.Task.WaitAsync(cancellationToken);
            call.Read = read;
            calls[index] = call;
            return new ValueTask<ServiceHealthSnapshot>(read);
        }

        /// <summary>
        /// Returns the recorded call once the source has received the token and registered its
        /// cancellation callback; both happen before the read task is published.
        /// </summary>
        internal async Task<SourceCall> WaitForCallAsync(int index)
        {
            var deadline = DateTimeOffset.UtcNow + Observation;
            while (!calls.TryGetValue(index, out var call))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The snapshot source was not entered.");
                await Task.Delay(10, Token);
            }

            return calls[index];
        }

        internal void Release() => release.TrySetResult(ReadySnapshot);

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
            return ValueTask.FromResult(ReadySnapshot);
        }
    }

    private sealed class SelfCancellingSource : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ServiceHealthSnapshot>(
                new OperationCanceledException("probe-secret", new CancellationToken(canceled: true)));
    }
}

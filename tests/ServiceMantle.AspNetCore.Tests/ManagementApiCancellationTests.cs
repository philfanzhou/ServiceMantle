using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.Health;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the cancellation and concurrency contract of the protected management API v1 group: the
/// caller's cancellation stays the caller's, the existing phase gate still notifies a cooperative
/// snapshot source, an internal cancellation is answered as a closed rejection instead, and two
/// concurrent requests share neither their Correlation ID nor their cancellation.
/// </summary>
public sealed class ManagementApiCancellationTests
{
    private const string Path = "/management/v1/ok";
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Caller_cancellation_notifies_the_source_and_never_runs_the_handler()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source);
        var abort = host.CreateAbort();
        try
        {
            var request = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, Path, abort.Token, "cancelled-request"),
                Token));
            var call = await source.WaitForCallAsync(0);

            await abort.CancelAsync();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            // The caller's own token, the gate's notification, and the source's token are separate
            // observations; none of them stands in for another.
            Assert.Equal(abort.Token, error.CancellationToken);
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, host.Recorder.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task An_already_cancelled_request_never_reads_the_source_or_runs_the_handler()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source);
        var abort = host.CreateAbort();
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, Path, abort.Token, "cancelled-request"),
                Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, host.Recorder.Calls);
    }

    [Fact]
    public async Task An_internal_timeout_notifies_the_source_and_is_answered_as_a_closed_rejection()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.CreateAsync(
            source: source,
            snapshotTimeout: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();
        try
        {
            using var response = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                "{\"errorCode\":\"service.phase.unavailable\"}",
                await response.Content.ReadAsStringAsync(Token));
            var call = await source.WaitForCallAsync(0);
            await call.CancellationObserved.Task.WaitAsync(Observation, Token);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            Assert.Equal(0, host.Recorder.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task An_internal_cancellation_inside_a_handler_stays_an_internal_error()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(
            "/management/v1/internal-cancel",
            host.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(
            ManagementApiHostFixture.Secret,
            await response.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_own_correlation_cancellation_and_single_snapshot()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source);
        var cancelled = host.CreateAbort();
        var admitted = host.CreateAbort();
        try
        {
            var cancelledRequest = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, Path, cancelled.Token, "first-request"),
                Token));
            var first = await source.WaitForCallAsync(0);
            var admittedRequest = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, Path, admitted.Token, "second-request"),
                Token));
            var second = await source.WaitForCallAsync(1);

            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
            Assert.True(first.CancellationObserved.Task.IsCompleted);
            Assert.False(second.CancellationObserved.Task.IsCompleted);
            Assert.False(second.ObservedToken.IsCancellationRequested);
            Assert.NotEqual(first.ObservedToken, second.ObservedToken);

            source.Release();
            var context = await admittedRequest.WaitAsync(Observation, Token);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(
                "second-request",
                Assert.Single(context.Response.Headers[ServiceHeaderNames.CorrelationId].ToArray()));
            Assert.False(second.ObservedToken.IsCancellationRequested);
            // The deterministic barrier proves each admitted request read the snapshot exactly once.
            Assert.Equal(2, source.Calls);
            Assert.Equal(1, host.Recorder.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task A_cancelled_request_inside_the_handler_never_answers_the_caller()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        var abort = host.CreateAbort();
        var request = host.Track(host.SendAsync(
            "/management/v1/hold",
            host.Cookie("admin", ManagementPermission.Admin),
            abort: abort));

        await host.Recorder.Entered.Task.WaitAsync(Observation, Token);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, host.Recorder.Calls);
    }

    private static void Configure(
        HttpContext context,
        ManagementApiHostFixture host,
        string path,
        CancellationToken abort,
        string correlationId)
    {
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers.Cookie = host.Cookie("admin", ManagementPermission.Admin);
        context.Request.Headers[ServiceHeaderNames.CorrelationId] = correlationId;
        context.RequestAborted = abort;
    }

    private sealed class SourceCall(CancellationToken observedToken)
    {
        internal CancellationToken ObservedToken { get; } = observedToken;

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenRegistration Registration { get; set; }

        internal Task<ServiceHealthSnapshot> Read { get; set; } =
            Task.FromException<ServiceHealthSnapshot>(new InvalidOperationException("The read was not started."));
    }

    /// <summary>
    /// A cooperative source that honours the token it receives and lets the test own release.
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
            call.Registration = cancellationToken.Register(() => call.CancellationObserved.TrySetResult());
            call.Read = release.Task.WaitAsync(cancellationToken);
            calls[index] = call;
            return new ValueTask<ServiceHealthSnapshot>(call.Read);
        }

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

        internal void Release() => release.TrySetResult(ManagementApiHostFixture.Ready);

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
}

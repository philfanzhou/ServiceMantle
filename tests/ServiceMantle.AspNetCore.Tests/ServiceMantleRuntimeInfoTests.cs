using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Http;
using ServiceMantle.Installation;
using ServiceMantle.Logging;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the protected management API v1 runtime information endpoint: its opt-in mapping, the
/// fixed four-field projection, the phase observation it reports, and the failure, cancellation,
/// concurrency and secret-free guarantees it inherits from the group it is mapped into.
/// </summary>
public sealed class ServiceMantleRuntimeInfoTests
{
    private const string Root = "/management/v1";
    private const string Path = Root + "/runtime";
    private const string MappingGuard =
        "The ServiceMantle management API v1 runtime information endpoint must be mapped at most once,";

    private static readonly string[] Fields = ["serviceName", "serviceVersion", "instanceId", "phase"];
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Action<RouteGroupBuilder> Map => static group => group.MapServiceMantleRuntimeInfo();

    public static IEnumerable<object[]> Matrix =>
        from phase in Enum.GetValues<ServiceStartupPhase>()
        from migration in Enum.GetValues<ServiceMigrationReadinessState>()
        from database in Enum.GetValues<ServiceDatabaseReadinessState>()
        select new object[] { phase, migration, database };

    [Theory]
    [InlineData(null, Path, Root + "/runtime/details")]
    [InlineData("/ops/admin/v1", "/ops/admin/v1/runtime", Path)]
    public async Task The_endpoint_follows_the_group_root_and_adds_nothing_else(
        string? root, string mapped, string unmapped)
    {
        await using var host = await ManagementApiHostFixture.StartAsync(root: root, children: Map);
        var cookie = host.Cookie("admin", ManagementPermission.Admin);
        using var found = await host.SendAsync(mapped, cookie);
        using var missing = await host.SendAsync(unmapped, cookie);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_baseline_group_alone_exposes_no_runtime_endpoint()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("outside-the-group")]
    [InlineData("look-alike-group")]
    [InlineData("nested-group")]
    public async Task A_repeated_or_misplaced_mapping_fails_before_a_successful_start(string scenario)
    {
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await using var host = await Create(scenario);
            await host.StartAsync();
        });

        Assert.StartsWith(MappingGuard, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagementApiHostFixture.Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admitted_response_carries_exactly_the_four_registered_identity_fields()
    {
        await using var host = await ManagementApiHostFixture.StartAsync(children: Map);
        var logContext = host.Application.Services.GetRequiredService<ServiceLogContext>();
        using var response = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(Fields, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(logContext.ServiceName, json.RootElement.GetProperty("serviceName").GetString());
        Assert.Equal(logContext.ServiceVersion, json.RootElement.GetProperty("serviceVersion").GetString());
        Assert.Equal(logContext.InstanceId, json.RootElement.GetProperty("instanceId").GetString());
        Assert.Equal("Completed", json.RootElement.GetProperty("phase").GetString());
        ManagementApiHostFixture.AssertSecurityHeaders(response);
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            Assert.Single(response.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId)));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Only_the_admitted_phase_reaches_the_projection(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var source = new ManagementApiHostFixture.SnapshotSource(new(phase, migration, database));
        await using var host = await ManagementApiHostFixture.StartAsync(source, children: Map);
        var admitted = phase == ServiceStartupPhase.Completed &&
            migration == ServiceMigrationReadinessState.Succeeded &&
            database == ServiceDatabaseReadinessState.Reachable;

        using var response = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(admitted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The gate reads the source once per request; the projection never reads it again.
        Assert.Equal(1, source.Calls);
        if (admitted)
        {
            Assert.Contains("\"phase\":\"Completed\"", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", body);
        }
    }

    [Fact]
    public async Task A_state_change_during_the_request_neither_retracts_it_nor_is_read_again()
    {
        var source = new PhaseFlipSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source, children: Map);
        var cookie = host.Cookie("admin", ManagementPermission.Admin);

        // The barrier is the source itself: it hands the gate the admitting snapshot and has already
        // moved to a rejecting one by the time that read returns.
        using var admitted = await host.SendAsync(Path, cookie);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Contains(
            "\"phase\":\"Completed\"",
            await admitted.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        Assert.Equal(1, source.Calls);

        using var rejected = await host.SendAsync(Path, cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData("none", 401, "management.session.unauthenticated")]
    [InlineData("corrupted", 401, "management.session.expired")]
    [InlineData("expired", 401, "management.session.expired")]
    [InlineData("read-only", 403, "management.session.forbidden")]
    [InlineData("invalid-claims", 403, "management.session.forbidden")]
    public async Task A_rejected_session_never_reaches_the_projection(
        string session, int status, string errorCode)
    {
        await using var host = await ManagementApiHostFixture.StartAsync(children: Map);
        using var response = await host.SendAsync(Path, Session(host, session));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"" + errorCode + "\"}",
            await response.Content.ReadAsStringAsync(Token));
        ManagementApiHostFixture.AssertSecurityHeaders(response);
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            Assert.Single(response.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId)));
    }

    [Fact]
    public async Task An_exhausted_management_quota_answers_before_the_projection()
    {
        await using var host = await ManagementApiHostFixture.StartAsync(
            managementPermitLimit: 1,
            children: Map);
        var cookie = host.Cookie("admin", ManagementPermission.Admin);
        using var accepted = await host.SendAsync(Path, cookie);
        using var rejected = await host.SendAsync(Path, cookie);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.DoesNotContain(
            "instanceId",
            await rejected.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        ManagementApiHostFixture.AssertSecurityHeaders(rejected);
    }

    [Fact]
    public async Task A_method_other_than_get_never_reaches_the_projection()
    {
        await using var host = await ManagementApiHostFixture.StartAsync(children: Map);
        using var response = await host.SendAsync(
            Path,
            host.Cookie("admin", ManagementPermission.Admin),
            method: HttpMethod.Post);

        // The framework's method rejection is answered by the existing gate baseline, unchanged.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"service.phase.unavailable\"}",
            await response.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("internal-cancel")]
    [InlineData("timeout")]
    public async Task A_missing_failing_or_timing_out_source_keeps_the_gate_rejection(string mode)
    {
        await using var host = await ManagementApiHostFixture.CreateAsync(
            source: mode == "absent" ? null : new FailureSource(mode),
            registerSource: mode != "absent",
            snapshotTimeout: TimeSpan.FromMilliseconds(50),
            children: Map);
        await host.StartAsync();
        using var response = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", body);
        Assert.DoesNotContain(ManagementApiHostFixture.Secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_cancelled_request_never_reads_the_source_or_projects()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source, children: Map);
        var abort = host.CreateAbort();
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, abort.Token, "cancelled-request"),
                Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_cancellation_during_the_observation_stays_the_callers_cancellation()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source, children: Map);
        var abort = host.CreateAbort();
        try
        {
            var request = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, abort.Token, "cancelled-request"),
                Token));
            var call = await source.WaitForCallAsync(0);

            await abort.CancelAsync();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(abort.Token, error.CancellationToken);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            Assert.Equal(1, source.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_own_correlation_id_and_cancellation()
    {
        var source = new CooperativeSource();
        await using var host = await ManagementApiHostFixture.StartAsync(source, children: Map);
        var cancelled = host.CreateAbort();
        var admitted = host.CreateAbort();
        try
        {
            var cancelledRequest = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, cancelled.Token, "first-request"),
                Token));
            var first = await source.WaitForCallAsync(0);
            var admittedRequest = host.Track(host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, admitted.Token, "second-request"),
                Token));
            var second = await source.WaitForCallAsync(1);

            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
            Assert.True(first.ObservedToken.IsCancellationRequested);
            Assert.False(second.ObservedToken.IsCancellationRequested);

            source.Release();
            var context = await admittedRequest.WaitAsync(Observation, Token);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(
                "second-request",
                Assert.Single(context.Response.Headers[ServiceMantleHeaderNames.CorrelationId].ToArray()));
            Assert.Equal(2, source.Calls);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Neither_the_projection_nor_its_rejections_expose_host_or_request_secrets()
    {
        var configurationKey = "ServiceMantle:Test:RuntimeInfoSecret";
        await using var host = await ManagementApiHostFixture.CreateAsync(
            source: new FailureSource("throw"),
            children: Map,
            outside: application => application.Configuration[configurationKey] = ManagementApiHostFixture.Secret);
        await host.StartAsync();
        var bootstrapFilePath = host.Application.Services
            .GetRequiredService<ServiceMantleRegistration>()
            .BootstrapFilePath;
        Assert.Equal(
            ManagementApiHostFixture.Secret,
            host.Application.Configuration[configurationKey]);

        using var rejected = await host.SendAsync(Path, host.Cookie("admin", ManagementPermission.Admin));
        var rejectedBody = await rejected.Content.ReadAsStringAsync(Token);

        await using var ready = await ManagementApiHostFixture.CreateAsync(
            children: Map,
            outside: application => application.Configuration[configurationKey] = ManagementApiHostFixture.Secret);
        await ready.StartAsync();
        using var forbidden = await ready.SendAsync(Path, ready.Cookie("reader", ManagementPermission.Read));
        using var admitted = await ready.SendAsync(Path, ready.Cookie("admin", ManagementPermission.Admin));
        var forbiddenBody = await forbidden.Content.ReadAsStringAsync(Token);
        var admittedBody = await admitted.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        foreach (var body in new[] { rejectedBody, forbiddenBody, admittedBody })
        {
            Assert.DoesNotContain(ManagementApiHostFixture.Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(bootstrapFilePath, body, StringComparison.Ordinal);
            Assert.DoesNotContain(configurationKey, body, StringComparison.Ordinal);
        }

        foreach (var response in new[] { rejected, forbidden, admitted })
        {
            Assert.DoesNotContain(ManagementApiHostFixture.Secret, Headers(response), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Development_and_production_answer_the_same_body()
    {
        await using var development = await ManagementApiHostFixture.StartAsync(
            environment: "Development",
            children: Map);
        await using var production = await ManagementApiHostFixture.StartAsync(
            environment: "Production",
            children: Map);
        using var first = await development.SendAsync(Path, development.Cookie("a", ManagementPermission.Admin));
        using var second = await production.SendAsync(Path, production.Cookie("a", ManagementPermission.Admin));

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Token),
            await second.Content.ReadAsStringAsync(Token));
    }

    private static Task<ManagementApiHostFixture> Create(string scenario) => scenario switch
    {
        "duplicate" => ManagementApiHostFixture.CreateAsync(children: group =>
        {
            group.MapServiceMantleRuntimeInfo();
            group.MapServiceMantleRuntimeInfo();
        }),
        "outside-the-group" => ManagementApiHostFixture.CreateAsync(
            outside: application => application.MapServiceMantleRuntimeInfo()),
        "look-alike-group" => ManagementApiHostFixture.CreateAsync(
            outside: application => application.MapGroup("/other/v1").MapServiceMantleRuntimeInfo()),
        "nested-group" => ManagementApiHostFixture.CreateAsync(
            children: group => group.MapGroup("/nested").MapServiceMantleRuntimeInfo()),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static void Configure(
        HttpContext context,
        ManagementApiHostFixture host,
        CancellationToken abort,
        string correlationId)
    {
        context.Request.Method = "GET";
        context.Request.Path = Path;
        context.Request.Headers.Cookie = host.Cookie("admin", ManagementPermission.Admin);
        context.Request.Headers[ServiceMantleHeaderNames.CorrelationId] = correlationId;
        context.RequestAborted = abort;
    }

    private static string? Session(ManagementApiHostFixture host, string session) => session switch
    {
        "none" => null,
        "corrupted" => ManagementApiHostFixture.CorruptedCookie(),
        "expired" => host.Cookie("admin", ManagementPermission.Admin, TimeSpan.FromMinutes(30)),
        "read-only" => host.Cookie("reader", ManagementPermission.Read),
        "invalid-claims" => host.CookieWithInvalidClaims(),
        _ => host.Cookie("admin", ManagementPermission.Admin)
    };

    private static string Headers(HttpResponseMessage response) =>
        string.Join(";", response.Headers.Concat(response.Content.Headers)
            .Select(header => header.Key + "=" + string.Join(",", header.Value)));

    private sealed class FailureSource(string mode) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            mode switch
            {
                "null" => ValueTask.FromResult<ServiceHealthSnapshot>(null!),
                "internal-cancel" => throw new OperationCanceledException(ManagementApiHostFixture.Secret),
                "timeout" => new(new TaskCompletionSource<ServiceHealthSnapshot>().Task),
                _ => throw new InvalidOperationException(ManagementApiHostFixture.Secret)
            };
    }

    /// <summary>
    /// Admits exactly one observation and moves to a rejecting state before that read returns.
    /// </summary>
    private sealed class PhaseFlipSource : IServiceHealthSnapshotSource
    {
        private static readonly ServiceHealthSnapshot Stopped = new(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable);

        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Interlocked.Increment(ref calls) == 1
                ? ManagementApiHostFixture.Ready
                : Stopped);
    }

    private sealed class SourceCall(CancellationToken observedToken)
    {
        internal CancellationToken ObservedToken { get; } = observedToken;

        internal Task<ServiceHealthSnapshot> Read { get; set; } =
            Task.FromException<ServiceHealthSnapshot>(new InvalidOperationException("The read was not started."));
    }

    /// <summary>A cooperative source that honours its token and lets the test own the release.</summary>
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
            var call = new SourceCall(cancellationToken) { Read = release.Task.WaitAsync(cancellationToken) };
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
            }
        }
    }
}

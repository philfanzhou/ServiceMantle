using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the phase admission marker for consumer endpoints outside the management prefix: a
/// marked endpoint is admitted by phase membership alone (readiness is not consulted), migration
/// running or failed still rejects, snapshot failures fail closed, the caller's cancellation wins,
/// the marker grants no authorization exemption, and invalid placements fail startup.
/// </summary>
public sealed class PhaseGatePhaseAdmissionTests
{
    private const string Secret = "Password=database-secret;SELECT private_data";
    private const string FixedFailure =
        "The ServiceMantle phase gate configuration or endpoint mapping is invalid.";
    private const string FixedBody = "{\"errorCode\":\"service.phase.unavailable\"}";
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Single_phase_marker_admits_the_declared_phase_regardless_of_readiness()
    {
        var source = new SwitchingSource(new ServiceHealthSnapshot(ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Unreachable));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        // An unready database does not keep the endpoint out of its declared phase.
        using var admitted = await client.GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(1, source.Calls);
        // A ready-but-other-phase snapshot still rejects: membership, not readiness, is decisive.
        source.Current = new ServiceHealthSnapshot(ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        using var pending = await client.GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        Assert.Equal(FixedBody, await pending.Content.ReadAsStringAsync(Token));
        source.Current = new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        using var completed = await client.GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completed.StatusCode);
        Assert.Equal(3, source.Calls);
    }

    public static IEnumerable<object[]> MemberPhases =>
    [
        [ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Unreachable],
        [ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Unreachable],
        [ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable]
    ];

    [Theory]
    [MemberData(nameof(MemberPhases))]
    public async Task Multi_phase_marker_admits_each_declared_member(ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration, ServiceDatabaseReadinessState database)
    {
        var source = new SwitchingSource(new ServiceHealthSnapshot(phase, migration, database));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            wide: true);
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var admitted = await client.GetAsync("/install-page", Token);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        source.Current = new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        using var completed = await client.GetAsync("/install-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completed.StatusCode);
        Assert.Equal(FixedBody, await completed.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData(ServiceMigrationReadinessState.Running)]
    [InlineData(ServiceMigrationReadinessState.Failed)]
    public async Task Migration_running_or_failed_rejects_even_when_the_phase_is_declared(
        ServiceMigrationReadinessState migration)
    {
        var source = new SwitchingSource(new ServiceHealthSnapshot(ServiceStartupPhase.BootstrapConfiguration,
            migration, ServiceDatabaseReadinessState.Reachable));
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        await app.StartAsync(Token);
        using var response = await app.GetTestClient().GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(FixedBody, await response.Content.ReadAsStringAsync(Token));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("factory")]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("null")]
    [InlineData("timeout")]
    public async Task Unavailable_snapshots_fail_closed_with_the_fixed_body(string mode)
    {
        await using var app = Build(mode == "missing"
            ? null
            : services => services.AddSingleton<IServiceHealthSnapshotSource>(_ =>
                mode == "factory" ? throw new InvalidOperationException(Secret) : new FailureSource(mode)),
            configure: options => options.SnapshotTimeout = TimeSpan.FromMilliseconds(50));
        await app.StartAsync(Token);
        using var response = await app.GetTestClient().GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(FixedBody, body);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_during_observation_fails_with_the_callers_token_and_never_runs_the_endpoint()
    {
        var source = new CooperativeSource();
        var executions = new Executions();
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            executions: executions);
        await app.StartAsync(Token);
        using var abort = new CancellationTokenSource();
        try
        {
            var request = app.GetTestServer().SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/bootstrap-page";
                context.RequestAborted = abort.Token;
            }, Token);
            var call = await source.WaitForCallAsync();
            abort.Cancel();
            // Reverse validation of the checkpoint: without the gate's caller-cancellation exit this
            // would surface as a 503 response instead of the caller's own cancellation.
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(abort.Token, error.CancellationToken);
            Assert.True(call.CancellationObserved.Task.IsCompleted);
            Assert.True(call.ObservedToken.IsCancellationRequested);
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, executions.Count);
        }
        finally
        {
            await source.ReleaseAsync();
        }
    }

    [Theory]
    [InlineData("under_prefix")]
    [InlineData("duplicate_markers")]
    [InlineData("empty_set")]
    [InlineData("undefined_value")]
    public async Task Invalid_marker_placement_fails_startup(string scenario)
    {
        await using var app = Build(map: false);
        if (scenario == "under_prefix")
            app.MapGet("/management/bootstrap-page", () => "page")
                .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration);
        if (scenario == "duplicate_markers")
            app.MapGet("/bootstrap-page", () => "page")
                .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration)
                .WithServiceMantlePhaseAdmission(ServiceStartupPhase.PendingSetup);
        if (scenario == "empty_set")
            app.MapGet("/bootstrap-page", () => "page").WithServiceMantlePhaseAdmission();
        if (scenario == "undefined_value")
            app.MapGet("/bootstrap-page", () => "page")
                .WithServiceMantlePhaseAdmission((ServiceStartupPhase)999);
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync(Token));
        Assert.Equal(FixedFailure, exception.Message);
    }

    [Fact]
    public async Task Admitted_marker_endpoint_still_faces_its_own_authorization()
    {
        var source = new SwitchingSource(new ServiceHealthSnapshot(ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Unreachable));
        await using var app = Build(
            services => services.AddSingleton<IServiceHealthSnapshotSource>(source),
            authorization: true);
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        // The gate admits the declared phase, and the endpoint's own policy still rejects the
        // unauthenticated caller: the marker grants no authorization exemption.
        using var admitted = await client.GetAsync("/protected-page", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, admitted.StatusCode);
        Assert.Equal(1, source.Calls);
        source.Current = new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        using var notAdmitted = await client.GetAsync("/protected-page", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notAdmitted.StatusCode);
    }

    [Fact]
    public async Task Concurrent_marked_requests_sample_once_each_and_the_next_request_observes_the_transition()
    {
        var source = new BarrierSource();
        await using var app = Build(services => services.AddSingleton<IServiceHealthSnapshotSource>(source));
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        var pending = Enumerable.Range(0, 12).Select(_ => client.GetAsync("/bootstrap-page", Token)).ToArray();
        await source.AllStarted.Task.WaitAsync(Token);
        source.Current = new ServiceHealthSnapshot(ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Unreachable);
        source.Release.TrySetResult();
        foreach (var response in await Task.WhenAll(pending))
        {
            using (response) Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        using var after = await client.GetAsync("/bootstrap-page", Token);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(13, source.Calls);
    }

    private static WebApplication Build(Action<IServiceCollection>? services = null,
        Action<PhaseGateOptions>? configure = null, bool map = true, bool wide = false,
        bool authorization = false, Executions? executions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var service = builder.Services.AddServiceMantle(ServiceId.Parse("admission"), InstanceId.Parse("admission-01"));
        service.AddServiceMantlePhaseGate(configure);
        if (authorization)
        {
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(AnonymousHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AnonymousHandler>(
                    AnonymousHandler.SchemeName,
                    _ => { });
        }
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseRouting();
        app.UseServiceMantlePhaseGate();
        // Explicit ordering keeps the gate ahead of authentication and authorization, matching the
        // composed ServiceMantle pipeline instead of relying on auto-insertion.
        if (authorization)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }
        if (map)
        {
            var management = app.MapServiceMantleManagementGroup();
            management.MapGet("/status", () => "status").WithServiceMantleManagementSurface(ManagementSurface.Status);
        }
        var tracker = executions ?? new Executions();
        app.MapGet("/bootstrap-page", () => tracker.Run("bootstrap"))
            .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration);
        if (wide)
            app.MapGet("/install-page", () => "install")
                .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration,
                    ServiceStartupPhase.PendingSetup);
        if (authorization)
            app.MapGet("/protected-page", () => "protected")
                .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration)
                .RequireAuthorization();
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

    private sealed class SwitchingSource(ServiceHealthSnapshot initial) : IServiceHealthSnapshotSource
    {
        private int calls;
        private ServiceHealthSnapshot current = initial;
        public ServiceHealthSnapshot Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }
        public int Calls => Volatile.Read(ref calls);
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(Current);
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

    private sealed class BarrierSource : IServiceHealthSnapshotSource
    {
        private int calls;
        private ServiceHealthSnapshot current = new(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable);
        public ServiceHealthSnapshot Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = Current;
            if (Interlocked.Increment(ref calls) == 12) AllStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
    }

    private sealed class SourceCall(CancellationToken observedToken)
    {
        internal CancellationToken ObservedToken { get; } = observedToken;
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration { get; set; }
        internal Task<ServiceHealthSnapshot> Read { get; set; } =
            Task.FromException<ServiceHealthSnapshot>(new InvalidOperationException("The read was not started."));
    }

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
            call.Registration = cancellationToken.Register(() => call.CancellationObserved.TrySetResult());
            call.Read = release.Task.WaitAsync(cancellationToken);
            calls[index] = call;
            return new ValueTask<ServiceHealthSnapshot>(call.Read);
        }

        internal async Task<SourceCall> WaitForCallAsync()
        {
            var deadline = DateTimeOffset.UtcNow + Observation;
            while (!calls.ContainsKey(0))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The snapshot source was not entered.");
                await Task.Delay(10, Token);
            }
            return calls[0];
        }

        internal async Task ReleaseAsync()
        {
            release.TrySetResult(new ServiceHealthSnapshot(ServiceStartupPhase.BootstrapConfiguration,
                ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Unreachable));
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

    /// <summary>
    /// An authentication handler that never authenticates, so the endpoint's own authorization
    /// policy is what answers once the phase gate has admitted the request.
    /// </summary>
    private sealed class AnonymousHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "ServiceMantle.Tests.Anonymous";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

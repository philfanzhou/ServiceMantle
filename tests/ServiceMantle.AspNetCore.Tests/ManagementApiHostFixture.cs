using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the protected management API v1 group and owns everything it creates.
/// </summary>
/// <remarks>
/// The fixture is shared by the group's normal-response tests and by its failure and cancellation
/// tests, so both observe the same wiring: one registration entry point, the composed ServiceMantle
/// pipeline, and test-only children mapped into the returned group. The children observe the fixed
/// baseline; they are not a product surface.
/// </remarks>
internal sealed class ManagementApiHostFixture : IAsyncDisposable
{
    internal const string Secret = "management-api-fake-secret";
    internal const string CorrelationId = "management-api-request";

    internal static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static readonly string[] ReadAndWriteMethods = ["GET", "POST"];

    private readonly List<CancellationTokenSource> aborts = [];
    private readonly List<Task> tracked = [];
    private WebApplication? application;
    private HttpClient? client;

    private ManagementApiHostFixture(WebApplication application, HandlerRecorder recorder)
    {
        this.application = application;
        Recorder = recorder;
    }

    internal HandlerRecorder Recorder { get; }

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Builds the host and, unless <paramref name="mapCount"/> is zero, maps the protected group.
    /// </summary>
    internal static async Task<ManagementApiHostFixture> CreateAsync(
        IServiceHealthSnapshotSource? source = null,
        bool registerSource = true,
        string? root = null,
        string? conflictingRoot = null,
        TimeSpan? snapshotTimeout = null,
        string? explicitGateRoot = null,
        TimeSpan? explicitGateTimeout = null,
        bool repeatRegistration = false,
        string authentication = "cookie",
        bool securityHeaders = true,
        bool rateLimiting = true,
        int managementPermitLimit = 120,
        string composition = "pipeline",
        int mapCount = 1,
        string environment = "Production",
        Action<RouteGroupBuilder>? children = null,
        Action<WebApplication>? outside = null)
    {
        var recorder = new HandlerRecorder();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Private-Test"]);
        if (securityHeaders) mantle.AddSecurityResponseHeaders();
        if (rateLimiting) mantle.AddRateLimiting(options => options.Management.PermitLimit = managementPermitLimit);
        if (authentication == "cookie") mantle.AddManagementCookieAuthentication();
        // A host that registers authentication services without any default scheme cannot challenge
        // or forbid, which the group's own startup check has to reject.
        if (authentication == "schemeless") builder.Services.AddAuthentication();

        Register(mantle, root, snapshotTimeout);
        if (repeatRegistration) Register(mantle, root, snapshotTimeout);
        if (conflictingRoot is not null) Register(mantle, conflictingRoot, snapshotTimeout);
        if (explicitGateRoot is not null || explicitGateTimeout is not null)
        {
            mantle.AddServiceMantlePhaseGate(options =>
            {
                options.ManagementPathPrefix =
                    explicitGateRoot ?? root ?? ServiceMantleManagementApiDefaults.DefaultRootPath;
                if (explicitGateTimeout is not null) options.SnapshotTimeout = explicitGateTimeout.Value;
            });
        }

        if (registerSource) builder.Services.AddSingleton(source ?? new SnapshotSource(Ready));

        var application = builder.Build();
        try
        {
            Compose(application, composition);
            for (var index = 0; index < mapCount; index++)
            {
                var group = application.MapServiceMantleManagementApiV1();
                if (index > 0) continue;
                MapChildren(group, recorder);
                children?.Invoke(group);
            }

            outside?.Invoke(application);
        }
        catch (Exception)
        {
            // A composition or mapping failure still has to release the host it was configuring.
            await using (application.ConfigureAwait(false))
            {
            }

            throw;
        }

        return new ManagementApiHostFixture(application, recorder);
    }

    internal static async Task<ManagementApiHostFixture> StartAsync(
        IServiceHealthSnapshotSource? source = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production",
        Action<RouteGroupBuilder>? children = null,
        Action<WebApplication>? outside = null)
    {
        var fixture = await CreateAsync(
            source: source,
            root: root,
            managementPermitLimit: managementPermitLimit,
            environment: environment,
            children: children,
            outside: outside);
        await fixture.StartAsync();
        return fixture;
    }

    internal async Task StartAsync()
    {
        await Application.StartAsync(Token);
        client = Application.GetTestClient();
    }

    /// <summary>Creates a cancellation source the fixture disposes with the host.</summary>
    internal CancellationTokenSource CreateAbort()
    {
        var abort = new CancellationTokenSource();
        aborts.Add(abort);
        return abort;
    }

    /// <summary>Tracks a request the test starts but may never await to completion.</summary>
    internal Task<T> Track<T>(Task<T> task)
    {
        tracked.Add(task);
        return task;
    }

    /// <summary>Protects a management ticket for the fixture's own ephemeral keys.</summary>
    internal string Cookie(string operatorId, ManagementPermission permission, TimeSpan? age = null)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId,
            [permission]);
        return Protect(identity.ToClaimsPrincipal(), age);
    }

    /// <summary>Protects a ticket that authenticates but carries no legitimate management identity.</summary>
    internal string CookieWithInvalidClaims() =>
        Protect(new ClaimsPrincipal(new ClaimsIdentity("ServiceMantle.Test")), null);

    internal static string CorruptedCookie() =>
        ServiceMantleManagementSessionDefaults.CookieName + "=not-a-protected-ticket";

    internal Task<HttpResponseMessage> SendAsync(
        string path,
        string? cookie = null,
        string? correlationId = CorrelationId,
        string[]? extraCorrelationIds = null,
        HttpMethod? method = null,
        bool secrets = true,
        CancellationTokenSource? abort = null)
    {
        method ??= HttpMethod.Get;
        var request = new HttpRequestMessage(method, path + (secrets ? "?token=" + Secret : ""));
        if (secrets)
        {
            request.Headers.Add("Authorization", "Bearer " + Secret);
            request.Headers.Add("X-Private-Test", Secret);
            request.Headers.Add("Cookie", "probe=" + Secret + (cookie is null ? "" : "; " + cookie));
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(
                    "{\"password\":\"" + Secret + "\"}",
                    Encoding.UTF8,
                    new MediaTypeHeaderValue("application/json"));
            }
        }
        else if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (correlationId is not null) request.Headers.Add("x-correlation-id", correlationId);
        foreach (var extra in extraCorrelationIds ?? []) request.Headers.Add("x-correlation-id", extra);
        return Client.SendAsync(request, abort?.Token ?? Token);
    }

    internal static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
            ["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
        })
        {
            Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var abort in aborts)
        {
            await abort.CancelAsync();
        }

        foreach (var task in tracked)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception)
            {
                // A tracked request may legitimately end cancelled or faulted; the fixture only has
                // to stop owning it before the host goes away.
            }
        }

        client?.Dispose();
        client = null;
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }

        foreach (var abort in aborts)
        {
            abort.Dispose();
        }
    }

    private static void Register(ServiceMantleBuilder mantle, string? root, TimeSpan? snapshotTimeout) =>
        mantle.AddServiceMantleManagementApiV1(options =>
        {
            if (root is not null) options.RootPath = root;
            if (snapshotTimeout is not null) options.SnapshotTimeout = snapshotTimeout.Value;
        });

    private string Protect(ClaimsPrincipal principal, TimeSpan? age)
    {
        var scheme = ServiceMantleManagementSessionDefaults.AuthenticationScheme;
        var options = Application.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(scheme);
        var issued = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero);
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued + TimeSpan.FromMinutes(5),
            },
            scheme);
        return ServiceMantleManagementSessionDefaults.CookieName + "=" +
            options.TicketDataFormat.Protect(ticket);
    }

    private static void Compose(WebApplication application, string composition)
    {
        switch (composition)
        {
            case "pipeline":
                application.UseServiceMantlePipeline();
                break;
            case "manual":
                // The same relative order assembled by hand: the phase gate is used exactly once, so
                // only the composed pipeline the group requires is missing.
                application.UseServiceMantleCorrelationId();
                application.UseServiceMantleProblemDetails();
                application.UseRouting();
                application.UseServiceMantleSecurityResponseHeaders();
                application.UseServiceMantlePhaseGate();
                application.UseAuthentication();
                application.UseRateLimiter();
                application.UseAuthorization();
                break;
        }
    }

    private static void MapChildren(RouteGroupBuilder group, HandlerRecorder recorder)
    {
        group.MapMethods("/ok", ReadAndWriteMethods, () => recorder.Run(Results.Ok(new { status = "ok" })));
        group.MapMethods("/no-content", ReadAndWriteMethods, () => recorder.Run(Results.NoContent()));
        group.MapMethods("/invalid", ReadAndWriteMethods,
            () => recorder.Run(ServiceMantleManagementApiResults.InvalidRequest()));
        group.MapMethods("/conflict", ReadAndWriteMethods,
            () => recorder.Run(ServiceMantleManagementApiResults.Conflict()));
        group.MapMethods("/exception", ReadAndWriteMethods, IResult () => recorder.Run<IResult>(() => throw Failure()));
        group.MapMethods("/internal-cancel", ReadAndWriteMethods, IResult () => recorder.Run<IResult>(
            () => throw new OperationCanceledException(Secret, new CancellationTokenSource().Token)));
        group.MapGet("/started", async (HttpContext context) =>
        {
            recorder.Count();
            await context.Response.WriteAsync("already-started", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            return ServiceMantleManagementApiResults.InvalidRequest();
        });
        group.MapGet("/hold", async (HttpContext context) =>
        {
            recorder.Count();
            recorder.Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            return Results.Ok();
        });
    }

    private static InvalidOperationException Failure()
    {
        var failure = new InvalidOperationException(Secret, new InvalidOperationException(Secret));
        failure.Data["probe"] = Secret;
        return failure;
    }

    internal sealed class HandlerRecorder
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Count() => Interlocked.Increment(ref calls);

        internal T Run<T>(T result)
        {
            Count();
            return result;
        }

        internal T Run<T>(Func<T> handler)
        {
            Count();
            return handler();
        }
    }

    internal sealed class SnapshotSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private int calls;
        private ServiceHealthSnapshot current = snapshot;

        internal int Calls => Volatile.Read(ref calls);

        internal ServiceHealthSnapshot Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(Current);
        }
    }
}

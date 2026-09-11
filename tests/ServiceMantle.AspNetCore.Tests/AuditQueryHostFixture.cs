using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Owns a protected management API host with only the opt-in audit query endpoint and its scoped
/// consumer-provided query service.
/// </summary>
internal sealed class AuditQueryHostFixture : IAsyncDisposable
{
    internal const string Secret = "audit-query-fake-secret";
    internal const string CorrelationId = "audit-query-request";
    internal const string Path = "/audit";

    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private readonly List<CancellationTokenSource> aborts = [];
    private readonly List<Task> tracked = [];
    private WebApplication? application;
    private HttpClient? client;

    private AuditQueryHostFixture(WebApplication application, string root)
    {
        this.application = application;
        Root = root;
    }

    internal string Root { get; }

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<AuditQueryHostFixture> CreateAsync(
        IManagementAuditQueryService? queryService = null,
        bool registerQueryService = true,
        ServiceHealthSnapshot? health = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production",
        int mapCount = 1,
        Action<RouteGroupBuilder>? children = null,
        Action<WebApplication>? outside = null,
        Func<IServiceProvider, IManagementAuditQueryService>? queryFactory = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            Service,
            InstanceId.Parse("orders-01"),
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Private-Test"]);
        mantle.AddSecurityResponseHeaders();
        mantle.AddRateLimiting(options => options.Management.PermitLimit = managementPermitLimit);
        mantle.AddManagementCookieAuthentication();
        mantle.AddServiceMantleManagementApiV1(options =>
        {
            if (root is not null)
            {
                options.RootPath = root;
            }
        });
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new HealthSource(health ?? Ready));
        if (registerQueryService)
        {
            builder.Services.AddScoped<IManagementAuditQueryService>(services =>
                queryFactory?.Invoke(services)
                ?? queryService
                ?? new RecordingQueryService());
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            var group = application.MapServiceMantleManagementApiV1();
            for (var index = 0; index < mapCount; index++)
            {
                group.MapServiceMantleAuditQueries();
            }

            children?.Invoke(group);
            outside?.Invoke(application);
        }
        catch (Exception)
        {
            await application.DisposeAsync();
            throw;
        }

        return new AuditQueryHostFixture(
            application,
            root ?? ManagementApiDefaults.DefaultRootPath);
    }

    internal static async Task<AuditQueryHostFixture> StartAsync(
        IManagementAuditQueryService? queryService = null,
        ServiceHealthSnapshot? health = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production",
        Func<IServiceProvider, IManagementAuditQueryService>? queryFactory = null)
    {
        var fixture = await CreateAsync(
            queryService,
            health: health,
            root: root,
            managementPermitLimit: managementPermitLimit,
            environment: environment,
            queryFactory: queryFactory);
        await fixture.StartAsync();
        return fixture;
    }

    internal async Task StartAsync()
    {
        await Application.StartAsync(Token);
        client = Application.GetTestClient();
    }

    internal CancellationTokenSource CreateAbort()
    {
        var abort = new CancellationTokenSource();
        aborts.Add(abort);
        return abort;
    }

    internal Task<T> Track<T>(Task<T> task)
    {
        tracked.Add(task);
        return task;
    }

    internal string Cookie(string operatorId, ManagementPermission permission, TimeSpan? age = null)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId,
            [permission]);
        return Protect(identity.ToClaimsPrincipal(), age);
    }

    internal string CookieWithInvalidClaims() =>
        Protect(new ClaimsPrincipal(new ClaimsIdentity("ServiceMantle.Test")), null);

    internal static string CorruptedCookie() =>
        ManagementSessionDefaults.CookieName + "=not-a-protected-ticket";

    internal string AdminCookie() => Cookie("admin", ManagementPermission.Admin);

    internal Task<HttpResponseMessage> SendAsync(
        string path = Path,
        string? cookie = null,
        string query = "",
        string? correlationId = CorrelationId,
        HttpMethod? method = null,
        bool secrets = false,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, Root + path + query);
        request.Content = content;
        if (secrets)
        {
            request.Headers.Add("Authorization", "Bearer " + Secret);
            request.Headers.Add("X-Private-Test", Secret);
            request.Headers.Add("Cookie", "probe=" + Secret + (cookie is null ? "" : "; " + cookie));
            request.Content ??= new StringContent(
                "{\"password\":\"" + Secret + "\"}",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));
        }
        else if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("x-correlation-id", correlationId);
        }

        return Client.SendAsync(request, Token);
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
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
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
                // Cancellation and handler failures are legitimate terminal states for tracked
                // requests; disposal only ensures the host no longer owns them.
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

    private string Protect(ClaimsPrincipal principal, TimeSpan? age)
    {
        var scheme = ManagementSessionDefaults.AuthenticationScheme;
        var options = Application.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(scheme);
        var issued = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero);
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued + TimeSpan.FromMinutes(5)
            },
            scheme);
        return ManagementSessionDefaults.CookieName + "="
            + options.TicketDataFormat.Protect(ticket);
    }

    private sealed class HealthSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    internal sealed class RecordingQueryService : IManagementAuditQueryService
    {
        private int calls;

        internal Func<ManagementAuditQuery, CancellationToken, ValueTask<ManagementAuditQueryResult>> Handler
        {
            get;
            set;
        } = static (query, _) => ValueTask.FromResult(
            new ManagementAuditQueryResult([], query.Page, query.PageSize, 0));

        internal int Calls => Volatile.Read(ref calls);

        internal List<ManagementAuditQuery> Queries { get; } = [];

        internal CancellationToken ObservedToken { get; private set; }

        public ValueTask<ManagementAuditQueryResult> QueryAsync(
            ManagementAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            lock (Queries)
            {
                Queries.Add(query);
            }

            ObservedToken = cancellationToken;
            return Handler(query, cancellationToken);
        }
    }
}

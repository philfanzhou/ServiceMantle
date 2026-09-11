using System.Net.Http.Headers;
using System.Security.Claims;
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
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

internal sealed class SettingUpdateHostFixture : IAsyncDisposable
{
    internal const string Secret = "setting-update-fake-secret";
    internal const string CorrelationId = "setting-update-request";
    internal const string Path = "/settings";

    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private readonly List<CancellationTokenSource> aborts = [];
    private readonly List<Task> tracked = [];
    private WebApplication? application;
    private HttpClient? client;

    private SettingUpdateHostFixture(WebApplication application, string root)
    {
        this.application = application;
        Root = root;
    }

    internal string Root { get; }
    internal WebApplication Application => application ?? throw new InvalidOperationException("The host was disposed.");
    internal HttpClient Client => client ?? throw new InvalidOperationException("The host was not started.");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<SettingUpdateHostFixture> CreateAsync(
        SettingUpdateExecutor? executor,
        ServiceHealthSnapshot? health = null,
        IManagementCurrentOperatorResolver? resolver = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production",
        int mapCount = 1,
        Action<RouteGroupBuilder>? children = null,
        Action<WebApplication>? outside = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(Service, InstanceId.Parse("orders-01"), "1.0");
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
        if (resolver is not null)
        {
            builder.Services.AddSingleton<IManagementCurrentOperatorResolver>(resolver);
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            var group = application.MapServiceMantleManagementApiV1();
            for (var index = 0; index < mapCount; index++)
            {
                group.MapServiceMantleSettingUpdates(executor);
            }

            children?.Invoke(group);
            outside?.Invoke(application);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }

        return new SettingUpdateHostFixture(
            application,
            root ?? ManagementApiDefaults.DefaultRootPath);
    }

    internal static async Task<SettingUpdateHostFixture> StartAsync(
        SettingUpdateExecutor executor,
        ServiceHealthSnapshot? health = null,
        IManagementCurrentOperatorResolver? resolver = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production")
    {
        var fixture = await CreateAsync(
            executor,
            health,
            resolver,
            root,
            managementPermitLimit,
            environment);
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
        var source = new CancellationTokenSource();
        aborts.Add(source);
        return source;
    }

    internal Task<T> Track<T>(Task<T> task)
    {
        tracked.Add(task);
        return task;
    }

    internal string AdminCookie() => Cookie("admin", ManagementPermission.Admin);

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

    internal Task<HttpResponseMessage> SendAsync(
        string body,
        string? cookie = null,
        string query = "",
        string contentType = "application/json",
        string? contentEncoding = null,
        string? correlationId = CorrelationId,
        HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, Root + Path + query)
        {
            Content = new StringContent(body)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (contentEncoding is not null)
        {
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
        }

        if (cookie is not null)
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
            catch
            {
                // Tracked requests may legitimately finish cancelled or faulted.
            }
        }

        client?.Dispose();
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

    internal sealed class RecordingExecutor
    {
        private int calls;

        internal Func<HttpContext, ServiceSettingUpdateCommand, CancellationToken,
            ValueTask<ServiceSettingUpdateResult>> Handler
        { get; set; } =
            static (_, command, _) => ValueTask.FromResult(
                ServiceSettingUpdateResult.Applied(command.ExpectedVersion + 1));

        internal int Calls => Volatile.Read(ref calls);
        internal List<ServiceSettingUpdateCommand> Commands { get; } = [];
        internal CancellationToken ObservedToken { get; private set; }

        internal ValueTask<ServiceSettingUpdateResult> ExecuteAsync(
            HttpContext context,
            ServiceSettingUpdateCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            lock (Commands)
            {
                Commands.Add(command);
            }

            ObservedToken = cancellationToken;
            return Handler(context, command, cancellationToken);
        }
    }
}

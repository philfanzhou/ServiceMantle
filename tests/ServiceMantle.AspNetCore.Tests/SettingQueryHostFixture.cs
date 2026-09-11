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
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the management API v1 setting query endpoints and owns everything it creates.
/// </summary>
/// <remarks>
/// The fixture exists for this task alone: the shared management API fixture cannot register the
/// consumer-owned setting catalog, snapshot source and root-key source these endpoints adapt. It
/// wires the same protection baseline — one registration entry point, the composed ServiceMantle
/// pipeline, and the protected group — and adds only the setting snapshot capability.
/// </remarks>
internal sealed class SettingQueryHostFixture : IAsyncDisposable
{
    internal const string Secret = "setting-query-fake-secret";
    internal const string CorrelationId = "setting-query-request";
    internal const string DefinitionsPath = "/settings/definitions";
    internal const string CurrentValuesPath = "/settings";

    internal static readonly ServiceId Service = ServiceId.Parse("orders-api");

    private static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private readonly List<CancellationTokenSource> aborts = [];
    private readonly List<Task> tracked = [];
    private WebApplication? application;
    private HttpClient? client;

    private readonly HealthSource healthSource;

    private SettingQueryHostFixture(WebApplication application, string root, HealthSource healthSource)
    {
        this.application = application;
        Root = root;
        this.healthSource = healthSource;
    }

    internal string Root { get; }

    /// <summary>Counts the phase-gate observations, one per admitted or rejected request.</summary>
    internal int GateReads => healthSource.Calls;

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<SettingQueryHostFixture> CreateAsync(
        IServiceSettingSnapshotSource? snapshotSource = null,
        IEnumerable<ServiceSettingDefinition>? definitions = null,
        IServiceSettingRootKeySource? rootKeySource = null,
        IServiceSettingCompositeValidator? compositeValidator = null,
        bool registerSettings = true,
        ServiceHealthSnapshot? health = null,
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
            if (root is not null) options.RootPath = root;
        });
        var healthSource = new HealthSource(health ?? Ready);
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(healthSource);

        if (registerSettings)
        {
            builder.Services.AddSingleton<IServiceSettingDefinitionProvider>(
                new DefinitionProvider(definitions ?? []));
            builder.Services.AddSingleton(snapshotSource ?? new RecordingSource(Read(0)));
            if (rootKeySource is not null) builder.Services.AddSingleton(rootKeySource);
            if (compositeValidator is not null) builder.Services.AddSingleton(compositeValidator);
            builder.Services.AddServiceMantleSettingSnapshots();
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            var group = application.MapServiceMantleManagementApiV1();
            for (var index = 0; index < mapCount; index++)
            {
                group.MapServiceMantleSettingQueries();
            }

            children?.Invoke(group);
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

        return new SettingQueryHostFixture(
            application,
            root ?? ManagementApiDefaults.DefaultRootPath,
            healthSource);
    }

    internal static async Task<SettingQueryHostFixture> StartAsync(
        IServiceSettingSnapshotSource? snapshotSource = null,
        IEnumerable<ServiceSettingDefinition>? definitions = null,
        IServiceSettingRootKeySource? rootKeySource = null,
        IServiceSettingCompositeValidator? compositeValidator = null,
        ServiceHealthSnapshot? health = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production")
    {
        var fixture = await CreateAsync(
            snapshotSource,
            definitions,
            rootKeySource,
            compositeValidator,
            health: health,
            root: root,
            managementPermitLimit: managementPermitLimit,
            environment: environment);
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
        ManagementSessionDefaults.CookieName + "=not-a-protected-ticket";

    internal string AdminCookie() => Cookie("admin", ManagementPermission.Admin);

    internal Task<HttpResponseMessage> SendAsync(
        string path,
        string? cookie = null,
        string query = "",
        string? correlationId = CorrelationId,
        HttpMethod? method = null,
        bool secrets = false,
        CancellationTokenSource? abort = null)
    {
        method ??= HttpMethod.Get;
        var request = new HttpRequestMessage(method, Root + path + query);
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

    internal static ServiceSettingSnapshotRead Read(long version, params PersistedServiceSettingValue[] values) =>
        new(Service, version, values);

    internal static PersistedServiceSettingValue Value(
        string key,
        long version,
        ServiceSettingValueType valueType,
        string value) => new(key, version, valueType, value);

    internal static string Protect(string key, string value, string rootKey) =>
        new SensitiveValueProtector(Service, key).Protect(value, rootKey);

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
                ExpiresUtc = issued + TimeSpan.FromMinutes(5),
            },
            scheme);
        return ManagementSessionDefaults.CookieName + "=" +
            options.TicketDataFormat.Protect(ticket);
    }

    private sealed class HealthSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class DefinitionProvider(IEnumerable<ServiceSettingDefinition> definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }

    /// <summary>A snapshot source whose read, failure and call count the test owns.</summary>
    internal sealed class RecordingSource(ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        private int calls;

        internal ServiceSettingSnapshotRead? Read { get; set; } = read;

        internal Exception? Failure { get; set; }

        internal int Calls => Volatile.Read(ref calls);

        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return Failure is null
                ? ValueTask.FromResult(Read!)
                : ValueTask.FromException<ServiceSettingSnapshotRead>(Failure);
        }
    }
}

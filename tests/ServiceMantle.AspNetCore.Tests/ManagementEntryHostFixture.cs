using System.Collections.Concurrent;
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
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the opt-in shared management entries and owns everything it creates.
/// </summary>
/// <remarks>
/// The entry handlers are side-effect-free counters, not a product surface: the fixture only has to
/// prove which requests reach a handler and which are stopped by the entry baseline.
/// </remarks>
internal sealed class ManagementEntryHostFixture : IAsyncDisposable
{
    internal static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    internal static readonly ManagementEntryKind[] AllKinds =
        Enum.GetValues<ManagementEntryKind>();

    private readonly List<CancellationTokenSource> aborts = [];
    private WebApplication? application;
    private HttpClient? client;

    private ManagementEntryHostFixture(WebApplication application, EntryRecorder recorder, string root)
    {
        this.application = application;
        Recorder = recorder;
        Root = root;
    }

    internal EntryRecorder Recorder { get; }

    internal string Root { get; }

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    internal SnapshotSource Source { get; private set; } = new(Ready);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<ManagementEntryHostFixture> CreateAsync(
        IServiceHealthSnapshotSource? source = null,
        string? root = null,
        bool entries = true,
        bool managementApi = true,
        bool cookieAuthentication = true,
        bool securityHeaders = true,
        bool rateLimiting = true,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120,
        string composition = "pipeline",
        ManagementEntryKind[]? map = null,
        Action<ManagementEntryKind, RouteHandlerBuilder>? configureEntry = null,
        Action<WebApplication, string>? extra = null)
    {
        var recorder = new EntryRecorder();
        var resolvedRoot = root ?? ManagementApiDefaults.DefaultRootPath;
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders();
        if (securityHeaders) mantle.AddSecurityResponseHeaders();
        if (rateLimiting)
        {
            mantle.AddRateLimiting(options =>
            {
                options.Setup.PermitLimit = setupPermitLimit;
                options.Management.PermitLimit = managementPermitLimit;
            });
        }

        if (cookieAuthentication) mantle.AddManagementCookieAuthentication();
        if (managementApi) mantle.AddServiceMantleManagementApiV1(options => options.RootPath = resolvedRoot);
        if (entries) mantle.AddServiceMantleManagementEntries();
        var snapshotSource = source as SnapshotSource ?? new SnapshotSource(Ready);
        builder.Services.AddSingleton(source ?? snapshotSource);

        var application = builder.Build();
        try
        {
            Compose(application, composition);
            foreach (var kind in map ?? AllKinds)
            {
                var entry = application.MapServiceMantleManagementEntry(kind, recorder.Handler(kind));
                configureEntry?.Invoke(kind, entry);
            }

            extra?.Invoke(application, resolvedRoot);
        }
        catch (Exception)
        {
            await using (application.ConfigureAwait(false))
            {
            }

            throw;
        }

        return new ManagementEntryHostFixture(application, recorder, resolvedRoot)
        {
            Source = snapshotSource,
        };
    }

    internal static async Task<ManagementEntryHostFixture> StartAsync(
        IServiceHealthSnapshotSource? source = null,
        string? root = null,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120,
        ManagementEntryKind[]? map = null,
        Action<WebApplication, string>? extra = null)
    {
        var fixture = await CreateAsync(
            source: source,
            root: root,
            setupPermitLimit: setupPermitLimit,
            managementPermitLimit: managementPermitLimit,
            map: map,
            extra: extra);
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

    internal static string Path(string root, ManagementEntryKind kind) => kind switch
    {
        ManagementEntryKind.InstallationStatus =>
            root + ManagementEntryDefaults.StatusPath,
        ManagementEntryKind.BootstrapCreate or ManagementEntryKind.BootstrapUpdate =>
            root + ManagementEntryDefaults.BootstrapPath,
        ManagementEntryKind.SetupStatus or ManagementEntryKind.SetupComplete =>
            root + ManagementEntryDefaults.SetupPath,
        ManagementEntryKind.SessionLogin =>
            root + ManagementEntryDefaults.SessionLoginPath,
        ManagementEntryKind.SessionLogout =>
            root + ManagementEntryDefaults.SessionLogoutPath,
        _ => root + ManagementEntryDefaults.SessionPath,
    };

    internal string Path(ManagementEntryKind kind) => Path(Root, kind);

    internal static HttpMethod Method(ManagementEntryKind kind) => kind switch
    {
        ManagementEntryKind.BootstrapUpdate => HttpMethod.Put,
        ManagementEntryKind.BootstrapCreate or ManagementEntryKind.SetupComplete or
            ManagementEntryKind.SessionLogin or ManagementEntryKind.SessionLogout =>
            HttpMethod.Post,
        _ => HttpMethod.Get,
    };

    /// <summary>Sends one conforming request for an entry kind.</summary>
    internal Task<HttpResponseMessage> SendAsync(
        ManagementEntryKind kind,
        string? cookie = null,
        HttpMethod? method = null,
        string? path = null,
        string?[]? unsafeHeader = null,
        CancellationTokenSource? abort = null)
    {
        method ??= Method(kind);
        var request = new HttpRequestMessage(method, path ?? Path(kind));
        var values = unsafeHeader ?? [ManagementEntryDefaults.UnsafeRequestHeaderValue];
        foreach (var value in values)
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    ManagementEntryDefaults.UnsafeRequestHeaderName,
                    value);
            }
        }

        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            request.Content = new StringContent(
                "{}",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));
        }

        return Client.SendAsync(request, abort?.Token ?? Token);
    }

    internal string Cookie(ManagementPermission permission, string operatorId = "operator-1", TimeSpan? age = null)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId,
            [permission]);
        return Protect(identity.ToClaimsPrincipal(), age);
    }

    internal string CookieWithInvalidClaims() =>
        Protect(new ClaimsPrincipal(new ClaimsIdentity("ServiceMantle.Test")), null);

    public async ValueTask DisposeAsync()
    {
        foreach (var abort in aborts)
        {
            await abort.CancelAsync();
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

    private static void Compose(WebApplication application, string composition)
    {
        switch (composition)
        {
            case "pipeline":
                application.UseServiceMantlePipeline();
                break;
            case "manual":
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

    /// <summary>Counts entry handler calls without performing any side effect.</summary>
    internal sealed class EntryRecorder
    {
        private readonly ConcurrentDictionary<ManagementEntryKind, int> calls = new();

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by a test to release a held handler.</summary>
        internal TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The single entry whose handler waits inside the request.</summary>
        internal ManagementEntryKind? Hold { get; set; }

        internal int Calls(ManagementEntryKind kind) =>
            calls.TryGetValue(kind, out var count) ? count : 0;

        internal int Total => calls.Values.Sum();

        internal Delegate Handler(ManagementEntryKind kind) =>
            async (HttpContext context) =>
            {
                calls.AddOrUpdate(kind, 1, (_, current) => current + 1);
                Entered.TrySetResult();
                if (Hold == kind)
                {
                    await Released.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
                }

                return Results.Json(new { entry = kind.ToString() });
            };
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

        internal void Reset() => Interlocked.Exchange(ref calls, 0);

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(Current);
        }
    }
}

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// Starts a host around the opt-in management session entries with a recording login adapter, and
/// owns everything it creates.
/// </summary>
internal sealed class ManagementSessionHostFixture : IAsyncDisposable
{
    /// <summary>Values a login must never leak back into a response, a header, or a log.</summary>
    internal const string SentinelPassword = "sentinel-password-value";

    internal const string SentinelProviderCode = "provider.sentinel_upstream_code";

    internal const string SentinelException = "sentinel-upstream-exception-detail";

    private readonly List<CancellationTokenSource> aborts = [];
    private WebApplication? application;
    private HttpClient? client;

    private ManagementSessionHostFixture(
        WebApplication application,
        RecordingLoginAdapter adapter,
        string root)
    {
        this.application = application;
        Adapter = adapter;
        Root = root;
    }

    internal RecordingLoginAdapter Adapter { get; }

    internal string Root { get; }

    internal string LoginPath => Root + ManagementEntryDefaults.SessionLoginPath;

    internal string SessionPath => Root + ManagementEntryDefaults.SessionPath;

    internal string LogoutPath => Root + ManagementEntryDefaults.SessionLogoutPath;

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<ManagementSessionHostFixture> CreateAsync(
        ServiceStartupPhase phase = ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState migrationStatus = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState databaseStatus = ServiceDatabaseReadinessState.Reachable,
        string? root = null,
        bool mapAdapter = true,
        int mapCount = 1,
        TimeSpan? loginTimeout = null,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120,
        string? keyRingDirectory = null,
        bool mapProtectedGroup = false)
    {
        var resolvedRoot = root ?? ManagementApiDefaults.DefaultRootPath;
        var adapter = new RecordingLoginAdapter();
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var dataProtection = builder.Services.AddDataProtection();
        if (keyRingDirectory is null)
        {
            dataProtection.UseEphemeralDataProtectionProvider();
        }
        else
        {
            // A shared key ring is what makes two instances accept each other's cookie.
            dataProtection
                .PersistKeysToFileSystem(new DirectoryInfo(keyRingDirectory))
                .SetApplicationName("ServiceMantle.SessionTests");
        }

        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders();
        mantle.AddSecurityResponseHeaders();
        mantle.AddRateLimiting(options =>
        {
            options.Setup.PermitLimit = setupPermitLimit;
            options.Management.PermitLimit = managementPermitLimit;
        });
        mantle.AddManagementCookieAuthentication();
        mantle.AddServiceMantleManagementApiV1(options => options.RootPath = resolvedRoot);
        mantle.AddServiceMantleManagementEntries();
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(
            new FixedHealthSource(new ServiceHealthSnapshot(phase, migrationStatus, databaseStatus)));

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            for (var index = 0; index < mapCount; index++)
            {
                application.MapServiceMantleManagementSession(
                    mapAdapter ? adapter.InvokeAsync : null,
                    options =>
                    {
                        if (loginTimeout is { } timeout)
                        {
                            options.LoginTimeout = timeout;
                        }
                    });
            }

            if (mapProtectedGroup)
            {
                application.MapServiceMantleManagementApiV1().MapServiceMantleRuntimeInfo();
            }
        }
        catch (Exception)
        {
            await using (application.ConfigureAwait(false))
            {
            }

            throw;
        }

        return new ManagementSessionHostFixture(application, adapter, resolvedRoot);
    }

    internal static async Task<ManagementSessionHostFixture> StartAsync(
        ServiceStartupPhase phase = ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState migrationStatus = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState databaseStatus = ServiceDatabaseReadinessState.Reachable,
        string? root = null,
        TimeSpan? loginTimeout = null,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120,
        string? keyRingDirectory = null,
        bool mapProtectedGroup = false)
    {
        var fixture = await CreateAsync(
            phase: phase,
            migrationStatus: migrationStatus,
            databaseStatus: databaseStatus,
            root: root,
            loginTimeout: loginTimeout,
            setupPermitLimit: setupPermitLimit,
            managementPermitLimit: managementPermitLimit,
            keyRingDirectory: keyRingDirectory,
            mapProtectedGroup: mapProtectedGroup);
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

    internal Task<HttpResponseMessage> LoginAsync(
        string? body = null,
        string? path = null,
        string?[]? unsafeHeader = null,
        string? contentEncoding = null,
        string? cookie = null,
        CancellationTokenSource? abort = null)
    {
        var request = Create(HttpMethod.Post, path ?? LoginPath, unsafeHeader, cookie);
        var content = new ByteArrayContent(
            Encoding.UTF8.GetBytes(body ?? "{\"password\":\"" + SentinelPassword + "\"}"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        request.Content = content;
        return Client.SendAsync(request, abort?.Token ?? Token);
    }

    internal Task<HttpResponseMessage> ReadSessionAsync(
        string? cookie = null,
        HttpMethod? method = null,
        string? path = null) =>
        Client.SendAsync(Create(method ?? HttpMethod.Get, path ?? SessionPath, [], cookie), Token);

    internal Task<HttpResponseMessage> LogoutAsync(
        string? cookie = null,
        string?[]? unsafeHeader = null,
        string? path = null) =>
        Client.SendAsync(Create(HttpMethod.Post, path ?? LogoutPath, unsafeHeader, cookie), Token);

    internal Task<HttpResponseMessage> GetAsync(string path, string? cookie = null) =>
        Client.SendAsync(Create(HttpMethod.Get, path, [], cookie), Token);

    /// <summary>Creates a protected cookie for an identity this host's key ring can read.</summary>
    internal string Cookie(
        ManagementPermission permission,
        string operatorId = "operator-1",
        TimeSpan? age = null) =>
        Cookie([permission], operatorId, age);

    internal string Cookie(
        ManagementPermission[] permissions,
        string operatorId = "operator-1",
        TimeSpan? age = null)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId,
            permissions,
            "sensitive-display-name");
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

    private HttpRequestMessage Create(
        HttpMethod method,
        string path,
        string?[]? unsafeHeader,
        string? cookie)
    {
        var request = new HttpRequestMessage(method, path);
        var values = unsafeHeader ??
            [ManagementEntryDefaults.UnsafeRequestHeaderValue];
        foreach (var value in values)
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    ManagementEntryDefaults.UnsafeRequestHeaderName,
                    value);
            }
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
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
                ExpiresUtc = issued + TimeSpan.FromMinutes(30),
            },
            scheme);
        return ManagementSessionDefaults.CookieName + "=" +
            options.TicketDataFormat.Protect(ticket);
    }

    /// <summary>
    /// Stands in for a consuming service's login adapter: it reads the body, remembers what it saw,
    /// and answers one configured identity result.
    /// </summary>
    internal sealed class RecordingLoginAdapter
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal List<string> Bodies { get; } = [];

        internal List<CancellationToken> Tokens { get; } = [];

        /// <summary>The result the adapter answers with; null exercises a null provider result.</summary>
        internal ManagementIdentityResult? Result { get; set; } =
            ManagementIdentityResult.Authenticated(ManagementIdentity.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                "operator-1",
                [ManagementPermission.Read, ManagementPermission.Admin],
                "sensitive-display-name"));

        /// <summary>Set to throw instead of answering.</summary>
        internal Exception? Failure { get; set; }

        /// <summary>Set to wait on the login token until it is cancelled by the budget or caller.</summary>
        internal bool Hang { get; set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask<ManagementIdentityResult> InvokeAsync(
            HttpContext httpContext,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            lock (Bodies)
            {
                Bodies.Add(body);
                Tokens.Add(cancellationToken);
            }

            Entered.TrySetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return Result!;
        }
    }

    private sealed class FixedHealthSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }
}

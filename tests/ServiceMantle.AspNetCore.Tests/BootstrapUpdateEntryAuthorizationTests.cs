using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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
/// Covers the authorization of the shared Bootstrap update entry: the conclusion must come from
/// this host's own fixed management cookie scheme and carry the administrator permission, while the
/// general administrator policy and the ordinary protected management group stay authentication
/// method agnostic.
/// </summary>
public sealed class BootstrapUpdateEntryAuthorizationTests
{
    private const string PhaseUnavailable = "service.phase.unavailable";
    private const string InvalidRequest = "management.request.invalid";
    private const string WideningPolicy = "ServiceMantle.Tests.WideningScheme";
    private const string StricterPolicy = "ServiceMantle.Tests.StricterPermission";

    private static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static readonly ServiceHealthSnapshot BeforeConfiguration = new(
        ServiceStartupPhase.BootstrapConfiguration,
        ServiceMigrationReadinessState.NotStarted,
        ServiceDatabaseReadinessState.Unreachable);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_host_without_the_fixed_cookie_scheme_cannot_map_the_bootstrap_update()
    {
        await using var host = Host.Create(cookie: false, externalDefaultScheme: true, root: "/mgmt/v1");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(host.StartAsync);

        Assert.DoesNotContain("/mgmt/v1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ExternalAdminHandler.SchemeName, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task An_external_default_scheme_never_authorizes_the_bootstrap_update()
    {
        await using var host = await Host.StartAsync(cookie: true, externalDefaultScheme: true);

        // The external handler authenticates every request as a legitimate management administrator,
        // and it is this host's default scheme. It still cannot reach the entry.
        using var external = await host.SendAsync();
        using var admin = await host.SendAsync(host.Cookie(ManagementPermission.Admin));
        using var reader = await host.SendAsync(host.Cookie(ManagementPermission.Read));
        using var expired = await host.SendAsync(
            host.Cookie(ManagementPermission.Admin, age: TimeSpan.FromHours(2)));
        using var damaged = await host.SendAsync(host.DamagedCookie());

        Assert.Equal(HttpStatusCode.Unauthorized, external.StatusCode);
        Assert.Equal(
            ManagementSessionDefaults.UnauthenticatedErrorCode,
            await ReadErrorCodeAsync(external));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Equal(
            ManagementSessionDefaults.ForbiddenErrorCode,
            await ReadErrorCodeAsync(reader));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(
            ManagementSessionDefaults.ExpiredErrorCode,
            await ReadErrorCodeAsync(expired));
        Assert.Equal(HttpStatusCode.Unauthorized, damaged.StatusCode);
        Assert.Equal(1, host.Calls);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("policy")]
    public async Task A_construction_that_widens_the_scheme_set_fails_at_startup(string construction)
    {
        await using var host = Host.Create(
            cookie: true,
            externalDefaultScheme: true,
            configureEntry: construction == "metadata"
                // A scheme named directly on the endpoint.
                ? entry => entry.WithMetadata(new AuthorizeAttribute
                {
                    AuthenticationSchemes = ExternalAdminHandler.SchemeName,
                })
                // A further policy that names one.
                : entry => entry.RequireAuthorization(WideningPolicy));

        await Assert.ThrowsAsync<InvalidOperationException>(host.StartAsync);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task A_stricter_additional_requirement_is_not_a_downgrade()
    {
        // The added policy names no scheme, so it tightens the rule without widening what may
        // authenticate the caller, and the host starts.
        await using var host = await Host.StartAsync(
            cookie: true,
            configureEntry: entry => entry.RequireAuthorization(StricterPolicy));

        using var admin = await host.SendAsync(host.Cookie(ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(1, host.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/mgmt/v1")]
    public async Task The_entry_keeps_its_fixed_shape_under_either_root(string? root)
    {
        await using var host = await Host.StartAsync(cookie: true, root: root);
        var cookie = host.Cookie(ManagementPermission.Admin);

        using var ok = await host.SendAsync(cookie);
        using var noHeader = await host.SendAsync(cookie, unsafeHeader: []);
        using var twoHeaders = await host.SendAsync(cookie, unsafeHeader: ["1", "1"]);
        using var noCookie = await host.SendAsync();

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(InvalidRequest, await ReadErrorCodeAsync(noHeader));
        Assert.Equal(HttpStatusCode.BadRequest, twoHeaders.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal(1, host.Calls);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("duplicate")]
    public async Task A_downgraded_or_repeated_mapping_fails_at_startup(string scenario)
    {
        await using var host = scenario == "anonymous"
            ? Host.Create(cookie: true, configureEntry: entry => entry.AllowAnonymous())
            : Host.Create(cookie: true, mapTwice: true);

        await Assert.ThrowsAsync<InvalidOperationException>(host.StartAsync);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task The_wrong_phase_answers_before_any_authentication()
    {
        await using var host = await Host.StartAsync(cookie: true, snapshot: BeforeConfiguration);

        // A cookie that would otherwise produce the closed expired-session 401 still gets the phase
        // result, so the gate answered before the cookie was read.
        using var damaged = await host.SendAsync(host.DamagedCookie());
        using var admin = await host.SendAsync(host.Cookie(ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, damaged.StatusCode);
        Assert.Equal(PhaseUnavailable, await ReadErrorCodeAsync(damaged));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, admin.StatusCode);
        Assert.Equal(PhaseUnavailable, await ReadErrorCodeAsync(admin));
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task The_named_rate_limit_still_bounds_the_entry()
    {
        await using var host = await Host.StartAsync(cookie: true, managementPermitLimit: 2);
        var cookie = host.Cookie(ManagementPermission.Admin);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await host.SendAsync(cookie);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.OK, statuses[1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(2, host.Calls);
    }

    [Fact]
    public async Task Two_concurrent_requests_never_share_one_identity()
    {
        await using var host = await Host.StartAsync(cookie: true);
        var admin = host.Cookie(ManagementPermission.Admin, "operator-admin");
        var reader = host.Cookie(ManagementPermission.Read, "operator-reader");

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => Task.Run(
                async () => await host.SendAsync(index % 2 == 0 ? admin : reader),
                Token)));

        try
        {
            Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.Forbidden));
            Assert.Equal(4, host.Calls);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Caller_cancellation_keeps_the_entry_result_out_of_the_client()
    {
        await using var host = await Host.StartAsync(cookie: true, hold: true);
        using var abort = new CancellationTokenSource();

        var pending = host.SendAsync(host.Cookie(ManagementPermission.Admin), abort: abort);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        host.Release();
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task A_host_that_does_not_map_the_entry_keeps_the_existing_public_contract()
    {
        // No management cookie capability at all: an external default scheme, the anonymous status
        // entry, and the ordinary protected management group.
        await using var host = await Host.StartAsync(
            cookie: false,
            externalDefaultScheme: true,
            mapBootstrapUpdate: false,
            mapStatusEntry: true,
            mapProtectedGroup: true);

        using var status = await host.Client.GetAsync(host.Root + "/status", Token);
        using var group = await host.Client.GetAsync(host.Root + "/probe", Token);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        // The general administrator policy is authentication-method agnostic, so a consuming
        // service's own external administrator still reaches the protected group.
        Assert.Equal(HttpStatusCode.OK, group.StatusCode);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Token);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("errorCode", out var value)
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// A host built for this file only, so the entry's authorization can be changed without
    /// touching the shared entry fixtures.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private WebApplication? application;
        private HttpClient? client;
        private int calls;

        private Host(WebApplication application, string root, bool hold)
        {
            this.application = application;
            Root = root;
            Hold = hold;
        }

        internal string Root { get; }

        private bool Hold { get; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls => Volatile.Read(ref calls);

        internal HttpClient Client =>
            client ?? throw new InvalidOperationException("The host was not started.");

        private WebApplication Application =>
            application ?? throw new InvalidOperationException("The host was disposed.");

        internal static Host Create(
            bool cookie,
            bool externalDefaultScheme = false,
            string? root = null,
            ServiceHealthSnapshot? snapshot = null,
            int managementPermitLimit = 120,
            bool mapBootstrapUpdate = true,
            bool mapStatusEntry = false,
            bool mapProtectedGroup = false,
            bool mapTwice = false,
            bool hold = false,
            Action<RouteHandlerBuilder>? configureEntry = null)
        {
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
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting(options => options.Management.PermitLimit = managementPermitLimit);
            if (cookie)
            {
                mantle.AddManagementCookieAuthentication();
            }

            mantle.AddServiceMantleManagementApiV1(options => options.RootPath = resolvedRoot);
            mantle.AddServiceMantleManagementEntries();
            if (externalDefaultScheme)
            {
                // Registered after the cookie capability, so the external handler really is this
                // host's default scheme and the cookie is only one of two registered schemes.
                builder.Services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ExternalAdminHandler.SchemeName;
                        options.DefaultChallengeScheme = ExternalAdminHandler.SchemeName;
                        options.DefaultForbidScheme = ExternalAdminHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ExternalAdminHandler>(
                        ExternalAdminHandler.SchemeName,
                        _ => { });
            }

            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(WideningPolicy, policy => policy
                    .AddAuthenticationSchemes(ExternalAdminHandler.SchemeName)
                    .RequireAuthenticatedUser())
                .AddPolicy(StricterPolicy, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ManagementPermissionRequirement(ManagementPermission.Admin)));
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(
                new FixedSnapshot(snapshot ?? Ready));

            var application = builder.Build();
            var host = new Host(application, resolvedRoot, hold);
            try
            {
                application.UseServiceMantlePipeline();
                if (mapStatusEntry)
                {
                    application.MapServiceMantleManagementEntry(
                        ManagementEntryKind.InstallationStatus,
                        host.Handler());
                }

                if (mapBootstrapUpdate)
                {
                    var entry = application.MapServiceMantleManagementEntry(
                        ManagementEntryKind.BootstrapUpdate,
                        host.Handler());
                    configureEntry?.Invoke(entry);
                    if (mapTwice)
                    {
                        application.MapServiceMantleManagementEntry(
                            ManagementEntryKind.BootstrapUpdate,
                            host.Handler());
                    }
                }

                if (mapProtectedGroup)
                {
                    application.MapServiceMantleManagementApiV1()
                        .MapGet("/probe", () => Results.Ok(new { probe = true }));
                }
            }
            catch (Exception)
            {
                application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }

            return host;
        }

        internal static async Task<Host> StartAsync(
            bool cookie,
            bool externalDefaultScheme = false,
            string? root = null,
            ServiceHealthSnapshot? snapshot = null,
            int managementPermitLimit = 120,
            bool mapBootstrapUpdate = true,
            bool mapStatusEntry = false,
            bool mapProtectedGroup = false,
            bool hold = false,
            Action<RouteHandlerBuilder>? configureEntry = null)
        {
            var host = Create(
                cookie,
                externalDefaultScheme,
                root,
                snapshot,
                managementPermitLimit,
                mapBootstrapUpdate,
                mapStatusEntry,
                mapProtectedGroup,
                mapTwice: false,
                hold,
                configureEntry);
            await host.StartAsync();
            return host;
        }

        internal async Task StartAsync()
        {
            await Application.StartAsync(Token);
            client = Application.GetTestClient();
        }

        internal void Release() => Released.TrySetResult();

        internal Task<HttpResponseMessage> SendAsync(
            string? cookie = null,
            HttpMethod? method = null,
            string?[]? unsafeHeader = null,
            CancellationTokenSource? abort = null)
        {
            var request = new HttpRequestMessage(
                method ?? HttpMethod.Put,
                Root + ManagementEntryDefaults.BootstrapPath);
            foreach (var value in unsafeHeader ??
                [ManagementEntryDefaults.UnsafeRequestHeaderValue])
            {
                request.Headers.TryAddWithoutValidation(
                    ManagementEntryDefaults.UnsafeRequestHeaderName,
                    value);
            }

            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            request.Content = new StringContent(
                "{}",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));
            return Client.SendAsync(request, abort?.Token ?? Token);
        }

        internal string Cookie(
            ManagementPermission permission,
            string operatorId = "operator-1",
            TimeSpan? age = null) =>
            Protect(
                ManagementIdentity.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    operatorId,
                    [permission]).ToClaimsPrincipal(),
                age);

        internal string DamagedCookie() =>
            ManagementSessionDefaults.CookieName + "=not-a-protected-ticket";

        public async ValueTask DisposeAsync()
        {
            Released.TrySetResult();
            client?.Dispose();
            client = null;
            if (application is not null)
            {
                await application.DisposeAsync();
                application = null;
            }
        }

        private Delegate Handler() => async (HttpContext context) =>
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            if (Hold)
            {
                await Released.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
            }

            return Results.Json(new { entry = "bootstrap-update" });
        };

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
    }

    /// <summary>
    /// A consuming service's own authentication handler that produces a legitimate management
    /// administrator for every request.
    /// </summary>
    private sealed class ExternalAdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "ServiceMantle.Tests.AuditExternal";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                ManagementIdentity.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    "external-operator",
                    [ManagementPermission.Admin]).ToClaimsPrincipal(),
                SchemeName)));
    }

    private sealed class FixedSnapshot(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }
}

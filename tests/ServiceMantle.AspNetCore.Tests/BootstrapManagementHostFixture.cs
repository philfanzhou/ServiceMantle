using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Audit;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the opt-in Bootstrap management entries with a real temporary Bootstrap
/// file, a real one-time credential store, and a recording candidate validator, and owns everything
/// it creates.
/// </summary>
/// <remarks>
/// The validator is recorded rather than real so the HTTP dispatch can be proved without a database.
/// The file store, the credential store, and the manager are the production ones.
/// </remarks>
internal sealed class BootstrapManagementHostFixture : IAsyncDisposable
{
    internal const string ConnectionString = "Host=db;Database=catalog;Password=fixture-password";

    internal const string MasterKey = "fixture-master-key";

    internal static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    internal static readonly ServiceHealthSnapshot BeforeConfiguration = new(
        ServiceStartupPhase.BootstrapConfiguration,
        ServiceMigrationReadinessState.NotStarted,
        ServiceDatabaseReadinessState.Unreachable);

    private static readonly ServiceId Service = ServiceId.Parse("catalog");

    private readonly string directory;
    private WebApplication? application;
    private HttpClient? client;

    private BootstrapManagementHostFixture(
        WebApplication application,
        string directory,
        string root,
        RecordingValidator validator,
        MutableSnapshot snapshot)
    {
        this.application = application;
        this.directory = directory;
        Root = root;
        Validator = validator;
        Snapshot = snapshot;
    }

    internal string Root { get; }

    internal RecordingValidator Validator { get; }

    internal MutableSnapshot Snapshot { get; }

    internal string BootstrapPath => Path.Combine(directory, "catalog.bootstrap.json");

    internal string CredentialPath => Path.Combine(directory, "catalog.bootstrap-credential.json");

    internal string EntryPath => Root + ServiceMantleManagementEntryDefaults.BootstrapPath;

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static BootstrapManagementHostFixture Create(
        string? root = null,
        bool credentialStore = true,
        bool cookieAuthentication = true,
        bool externalDefaultScheme = false,
        bool mapStatus = false,
        int mapCount = 1,
        ServiceHealthSnapshot? snapshot = null,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120)
    {
        var resolvedRoot = root ?? ServiceMantleManagementApiDefaults.DefaultRootPath;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"sm-bootstrap-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bootstrapPath = Path.Combine(directory, "catalog.bootstrap.json");
        var credentialPath = Path.Combine(directory, "catalog.bootstrap-credential.json");
        var validator = new RecordingValidator();
        var source = new MutableSnapshot(snapshot ?? BeforeConfiguration);

        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            Service,
            InstanceId.Parse("catalog-01"),
            bootstrapFilePath: bootstrapPath,
            serviceVersion: "1.0");
        mantle.AddSecurityResponseHeaders();
        mantle.AddRateLimiting(options =>
        {
            options.Setup.PermitLimit = setupPermitLimit;
            options.Management.PermitLimit = managementPermitLimit;
        });
        if (cookieAuthentication)
        {
            mantle.AddManagementCookieAuthentication();
        }

        mantle.AddServiceMantleManagementApiV1(options => options.RootPath = resolvedRoot);
        mantle.AddServiceMantleBootstrapManagement();
        if (mapStatus)
        {
            mantle.AddServiceMantleInstallationStatus();
        }

        if (externalDefaultScheme)
        {
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

        builder.Services.AddSingleton<IBootstrapCandidateValidator>(validator);
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(source);
        if (credentialStore)
        {
            builder.Services.AddSingleton<IBootstrapCredentialStore>(
                new BootstrapCredentialFileStore(Service, credentialPath, bootstrapPath));
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            for (var index = 0; index < mapCount; index++)
            {
                application.MapServiceMantleBootstrap();
            }

            if (mapStatus)
            {
                application.MapServiceMantleInstallationStatus();
            }
        }
        catch (Exception)
        {
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return new BootstrapManagementHostFixture(
            application,
            directory,
            resolvedRoot,
            validator,
            source);
    }

    internal static async Task<BootstrapManagementHostFixture> StartAsync(
        string? root = null,
        bool cookieAuthentication = true,
        bool externalDefaultScheme = false,
        bool mapStatus = false,
        ServiceHealthSnapshot? snapshot = null,
        int setupPermitLimit = 60,
        int managementPermitLimit = 120)
    {
        var fixture = Create(
            root: root,
            cookieAuthentication: cookieAuthentication,
            externalDefaultScheme: externalDefaultScheme,
            mapStatus: mapStatus,
            snapshot: snapshot,
            setupPermitLimit: setupPermitLimit,
            managementPermitLimit: managementPermitLimit);
        await fixture.StartAsync();
        return fixture;
    }

    internal async Task StartAsync()
    {
        await Application.StartAsync(Token);
        client = Application.GetTestClient();
    }

    /// <summary>Provisions one real credential through the registered store.</summary>
    internal async Task<string> ProvisionAsync()
    {
        var store = Application.Services.GetRequiredService<IBootstrapCredentialStore>();
        var result = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        Assert.True(result.IsProvisioned);
        return result.Credential!.Reveal();
    }

    /// <summary>Sends one conforming request unless a part is deliberately replaced.</summary>
    internal Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string? body = null,
        string? contentType = "application/json",
        string?[]? credential = null,
        string?[]? unsafeHeader = null,
        string? cookie = null,
        string? path = null,
        string? contentEncoding = null,
        CancellationTokenSource? abort = null)
    {
        var request = new HttpRequestMessage(method, path ?? EntryPath);
        foreach (var value in unsafeHeader ??
            [ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderValue])
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderName,
                    value);
            }
        }

        foreach (var value in credential ?? [])
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "X-ServiceMantle-Bootstrap-Credential",
                    value);
            }
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? CreateBody()));
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        request.Content = content;
        return Client.SendAsync(request, abort?.Token ?? Token);
    }

    internal Task<HttpResponseMessage> PostAsync(string? credential, string? body = null) =>
        SendAsync(HttpMethod.Post, body, credential: credential is null ? [] : [credential]);

    internal Task<HttpResponseMessage> PutAsync(string? cookie, string? body = null) =>
        SendAsync(HttpMethod.Put, body, cookie: cookie);

    internal static string CreateBody(
        string? provider = "PostgreSQL",
        string? connectionString = ConnectionString,
        string? masterKey = MasterKey) =>
        $$"""
        {"database":{"provider":"{{provider}}","connectionString":"{{connectionString}}"},"masterKey":"{{masterKey}}"}
        """;

    internal string Cookie(ManagementPermission permission, string operatorId = "operator-1")
    {
        var scheme = ServiceMantleManagementSessionDefaults.AuthenticationScheme;
        var options = Application.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(scheme);
        var issued = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(
            ManagementIdentity.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId,
                [permission]).ToClaimsPrincipal(),
            new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued + TimeSpan.FromMinutes(5),
            },
            scheme);
        return ServiceMantleManagementSessionDefaults.CookieName + "=" +
            options.TicketDataFormat.Protect(ticket);
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        client = null;
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Answers one fixed validation outcome and records every candidate it saw.</summary>
    internal sealed class RecordingValidator : IBootstrapCandidateValidator
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        /// <summary>Set to reject every candidate with this code.</summary>
        internal string? RejectionCode { get; set; }

        /// <summary>Set to fail every validation with this exception.</summary>
        internal Exception? Failure { get; set; }

        /// <summary>Set to return no result at all.</summary>
        internal bool ReturnsNull { get; set; }

        /// <summary>Runs inside the validation, before the file is written.</summary>
        internal Func<BootstrapConfiguration, ValueTask>? Before { get; set; }

        internal string? LastProvider { get; private set; }

        internal string? LastConnectionString { get; private set; }

        internal string? LastMasterKey { get; private set; }

        internal string? LastServerVersion { get; private set; }

        public async ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            LastProvider = candidate.Database.Provider;
            LastConnectionString = candidate.Database.ConnectionString;
            LastServerVersion = candidate.Database.ServerVersion;
            LastMasterKey = candidate.MasterKey;
            if (Before is { } before)
            {
                await before(candidate);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            if (ReturnsNull)
            {
                return null!;
            }

            return RejectionCode is { } code
                ? BootstrapValidationResult.Failure(code)
                : BootstrapValidationResult.Success();
        }
    }

    internal sealed class MutableSnapshot(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private ServiceHealthSnapshot current = snapshot;

        internal ServiceHealthSnapshot Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
    }

    /// <summary>A consuming service's own scheme that authenticates a management administrator.</summary>
    internal sealed class ExternalAdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "ServiceMantle.Tests.BootstrapExternal";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                ManagementIdentity.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    "external-operator",
                    [ManagementPermission.Admin]).ToClaimsPrincipal(),
                SchemeName)));
    }
}

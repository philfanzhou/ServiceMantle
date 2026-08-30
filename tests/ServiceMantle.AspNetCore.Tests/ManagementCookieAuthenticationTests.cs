using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ManagementCookieAuthenticationTests
{
    [Fact]
    public async Task Defaults_AreSecureFixedAndCarryNoDeploymentIdentity()
    {
        using var host = await StartHostAsync();
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ServiceMantleManagementSessionDefaults.AuthenticationScheme);

        Assert.Equal("ServiceMantle.ManagementCookie", ServiceMantleManagementSessionDefaults.AuthenticationScheme);
        Assert.Equal("__Host-ServiceMantle.Management", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.True(options.Cookie.IsEssential);
        Assert.Null(options.Cookie.Domain);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Equal(
            TimeSpan.FromHours(ServiceMantleManagementSessionDefaults.DefaultExpireTimeSpanHours),
            options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.DoesNotContain("catalog", options.Cookie.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog-01", options.Cookie.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Action<ServiceMantleManagementCookieOptions>> UnsafeSettings => new()
    {
        options => options.HttpOnly = false,
        options => options.SecurePolicy = CookieSecurePolicy.None,
        options => options.SecurePolicy = CookieSecurePolicy.SameAsRequest,
        options => options.SameSite = SameSiteMode.None,
        options => options.IsEssential = false,
        options => options.ExpireTimeSpan = TimeSpan.Zero,
        options => options.ExpireTimeSpan = TimeSpan.FromSeconds(-1),
        options => options.ExpireTimeSpan = TimeSpan.FromHours(
            ServiceMantleManagementSessionDefaults.MaximumExpireTimeSpanHours) + TimeSpan.FromTicks(1),
    };

    [Theory]
    [MemberData(nameof(UnsafeSettings))]
    public async Task UnsafeOverrides_FailWhenTheHostStarts(
        Action<ServiceMantleManagementCookieOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddManagementCookieAuthentication(configure);

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplicationName_IsStableDistinctAndNeverLogged()
    {
        var firstLogs = new RecordingLoggerProvider();
        var secondLogs = new RecordingLoggerProvider();
        var otherLogs = new RecordingLoggerProvider();

        using var first = await StartHostAsync("catalog", firstLogs);
        using var second = await StartHostAsync("catalog", secondLogs);
        using var other = await StartHostAsync("billing", otherLogs);

        var firstName = first.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator;
        var secondName = second.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator;
        var otherName = other.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator;

        Assert.NotNull(firstName);
        Assert.Equal(firstName, secondName);
        Assert.NotEqual(firstName, otherName);
        Assert.DoesNotContain(firstLogs.Messages, message => ContainsOrdinal(message, "catalog") ||
            ContainsOrdinal(message, firstName));
        Assert.DoesNotContain(secondLogs.Messages, message => ContainsOrdinal(message, "catalog") ||
            ContainsOrdinal(message, secondName));
        Assert.DoesNotContain(otherLogs.Messages, message => ContainsOrdinal(message, "billing") ||
            ContainsOrdinal(message, otherName));
    }

    [Fact]
    public async Task EquivalentRegistrationsAreIdempotentAndDifferentSettingsFailAtStartup()
    {
        var duplicateBuilder = Host.CreateApplicationBuilder();
        var duplicate = duplicateBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        duplicate.AddManagementCookieAuthentication(options =>
        {
            options.ExpireTimeSpan = TimeSpan.FromHours(4);
            options.SlidingExpiration = false;
        });
        duplicate.AddManagementCookieAuthentication(options =>
        {
            options.SlidingExpiration = false;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(240);
        });

        using (var duplicateHost = duplicateBuilder.Build())
        {
            await duplicateHost.StartAsync(TestContext.Current.CancellationToken);
            var schemes = await duplicateHost.Services
                .GetRequiredService<IAuthenticationSchemeProvider>()
                .GetAllSchemesAsync();
            Assert.Single(schemes, scheme =>
                scheme.Name == ServiceMantleManagementSessionDefaults.AuthenticationScheme);
            var options = duplicateHost.Services
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(ServiceMantleManagementSessionDefaults.AuthenticationScheme);
            Assert.Equal(TimeSpan.FromHours(4), options.ExpireTimeSpan);
            Assert.False(options.SlidingExpiration);
            await duplicateHost.StopAsync(TestContext.Current.CancellationToken);
        }

        var conflictBuilder = Host.CreateApplicationBuilder();
        var conflict = conflictBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        conflict.AddManagementCookieAuthentication();
        conflict.AddManagementCookieAuthentication(options => options.SlidingExpiration = false);

        using var conflictHost = conflictBuilder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            conflictHost.StartAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagementGroup_RequiresAdminUnlessExplicitlyAnonymous()
    {
        await using var application = await StartWebApplicationAsync(TimeSpan.FromMinutes(5));
        using var client = CreateClient(application);
        var readCookie = await SignInAsync(client, ManagementPermission.Read, "sensitive-reader");

        using var forbidden = await SendWithCookieAsync(client, "/management/protected", readCookie);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ForbiddenErrorCode,
            await ReadErrorCodeAsync(forbidden));
        AssertSafeSessionResponse(forbidden, readCookie, "sensitive-reader");

        using var anonymous = await client.GetAsync(
            "/management/public",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public async Task SessionResponses_DistinguishMissingExpiredAndForbiddenWithoutSensitiveMaterial()
    {
        await using var application = await StartWebApplicationAsync(TimeSpan.FromSeconds(1));
        using var client = CreateClient(application);

        using var missing = await client.GetAsync(
            "/management/protected",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode,
            await ReadErrorCodeAsync(missing));
        AssertSafeSessionResponse(missing, "cookie-plaintext", "sensitive-admin");

        var readCookie = await SignInAsync(client, ManagementPermission.Read, "sensitive-reader");
        using var forbidden = await SendWithCookieAsync(client, "/management/protected", readCookie);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ForbiddenErrorCode,
            await ReadErrorCodeAsync(forbidden));
        AssertSafeSessionResponse(forbidden, readCookie, "sensitive-reader");

        var adminCookie = await SignInAsync(client, ManagementPermission.Admin, "sensitive-admin");
        using (var accepted = await SendWithCookieAsync(client, "/management/protected", adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
        using var expired = await SendWithCookieAsync(client, "/management/protected", adminCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ExpiredErrorCode,
            await ReadErrorCodeAsync(expired));
        AssertSafeSessionResponse(expired, adminCookie, "sensitive-admin");
    }

    private static async Task<IHost> StartHostAsync(
        string serviceId = "catalog",
        ILoggerProvider? loggerProvider = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Services
            .AddServiceMantle(ServiceId.Parse(serviceId), InstanceId.Parse($"{serviceId}-01"))
            .AddManagementCookieAuthentication();
        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<WebApplication> StartWebApplicationAsync(TimeSpan expiration)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddManagementCookieAuthentication(options => options.ExpireTimeSpan = expiration);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();

        application.MapPost("/sign-in/{permission}", async (HttpContext context, string permission) =>
        {
            var managementPermission = Enum.Parse<ManagementPermission>(permission, ignoreCase: true);
            var identity = ManagementIdentity.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                context.Request.Query["operator"].ToString(),
                [managementPermission],
                "sensitive-display-name");
            await context.SignInAsync(
                ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                identity.ToClaimsPrincipal());
            return Results.NoContent();
        }).AllowAnonymous();

        var management = application.MapGroup("/management")
            .RequireServiceMantleManagementAdmin();
        management.MapGet("/protected", () => Results.Ok());
        management.MapGet("/public", () => Results.Ok()).AllowAnonymous();

        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private static HttpClient CreateClient(WebApplication application) => new()
    {
        BaseAddress = new Uri(application.Urls.Single()),
    };

    private static async Task<string> SignInAsync(
        HttpClient client,
        ManagementPermission permission,
        string operatorId)
    {
        using var response = await client.PostAsync(
            $"/sign-in/{permission}?operator={Uri.EscapeDataString(operatorId)}",
            content: null,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> SendWithCookieAsync(
        HttpClient client,
        string path,
        string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["errorCode"], document.RootElement.EnumerateObject().Select(property => property.Name));
        return document.RootElement.GetProperty("errorCode").GetString()!;
    }

    private static void AssertSafeSessionResponse(
        HttpResponseMessage response,
        string cookieMaterial,
        string claimMaterial)
    {
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
        Assert.DoesNotContain("text/html", response.Content.Headers.ContentType?.ToString() ?? string.Empty);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
        {
            Assert.DoesNotContain(setCookieValues, value =>
                value.Contains("Expires=", StringComparison.OrdinalIgnoreCase));
        }

        var publicProjection = string.Join(
            '\n',
            response.Headers.SelectMany(header => header.Value.Prepend(header.Key))
                .Concat(response.Content.Headers.SelectMany(header => header.Value.Prepend(header.Key)))
                .Append(response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult()));
        Assert.DoesNotContain(cookieMaterial, publicProjection, StringComparison.Ordinal);
        Assert.DoesNotContain(claimMaterial, publicProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-display-name", publicProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("servicemantle.operator_id", publicProjection, StringComparison.Ordinal);
    }

    private static bool ContainsOrdinal(string message, string? value) =>
        value is not null && message.Contains(value, StringComparison.Ordinal);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the phase gate's route classification of attribute-routed MVC endpoints. Their combined
/// template never carries the leading slash even though it stays root relative, so it has to reach
/// the same prefix comparison as a minimal API route text instead of failing
/// <see cref="Microsoft.AspNetCore.Http.PathString"/> construction while the host starts.
/// </summary>
public sealed class PhaseGateMvcRouteTests
{
    private const string Prefix = "/management";
    private const string VersionedRoot = "/management/v1";
    private const string FixedFailure =
        "The ServiceMantle phase gate configuration or endpoint mapping is invalid.";
    private static readonly ServiceHealthSnapshot Ready = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> RouteShapes =>
    [
        new object[] { "/management/settings", Prefix, true },
        new object[] { "/management", Prefix, true },
        new object[] { "/management/", Prefix, true },
        new object[] { "/MANAGEMENT/settings", Prefix, true },
        new object[] { "/management/v1/settings", Prefix, true },
        new object[] { "/manage", Prefix, false },
        new object[] { "/managementx", Prefix, false },
        new object[] { "/api/admin/session/login", Prefix, false },
        new object[] { "/health/live", Prefix, false },
        new object[] { "/health/live", "/health", true },
        new object[] { "/status/deep/segment", "/status", true },
        new object[] { "/management/v1/settings", VersionedRoot, true },
        new object[] { "/management/v1/status", VersionedRoot, true },
        new object[] { "/api/admin/session/login", VersionedRoot, false },
    ];

    public static IEnumerable<object[]> SurfaceMatrix =>
        from surface in Enum.GetValues<ManagementSurface>()
        from path in new[]
        {
            "/management/v1",
            "/management/v1/settings",
            "/management/v1/status",
            "/management/v1/status/deep",
            "/management/v1/bootstrap",
            "/management/v1/bootstrap/deep",
            "/management/v1/setup",
            "/management/v1/setup/deep",
            "/management/v2/settings",
            "/business",
            "/",
        }
        select new object[] { surface, path, ExpectedSurfaceMatch(surface, path) };

    [Fact]
    public async Task Attribute_routed_controllers_expose_route_text_without_a_leading_slash()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(Token);
        var texts = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Contains("api/admin/session/login", texts);
        Assert.Contains("management/unclassified", texts);
        Assert.All(texts, text => Assert.False(string.IsNullOrEmpty(text) || text!.StartsWith('/'),
            $"'{text}' unexpectedly carries a leading slash."));
    }

    [Fact]
    public async Task Attribute_routed_controllers_outside_the_prefix_start_and_are_admitted_by_the_gate()
    {
        await using var app = BuildGateHost(typeof(BusinessSessionController), Ready);
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var login = await client.GetAsync("/api/admin/session/login", Token);
        using var logout = await client.PostAsync("/api/admin/session/logout", null, Token);
        using var status = await client.GetAsync("/management/status", Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("login", await login.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal("logout", await logout.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    [Theory]
    [InlineData("attribute_routed_controller")]
    [InlineData("minimal_api")]
    public async Task Unmarked_endpoint_inside_the_prefix_fails_closed_with_the_fixed_message(string scenario)
    {
        await using var app = BuildGateHost(
            scenario == "attribute_routed_controller" ? typeof(UnmarkedManagementController) : null,
            mapGroup: false);
        if (scenario == "minimal_api") app.MapGet("/management/unclassified", () => "unclassified");
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync(Token));
        Assert.Equal(FixedFailure, exception.Message);
        Assert.DoesNotContain("must start with", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unclassified", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RouteShapes))]
    public void Prefix_comparison_is_identical_for_both_leading_slash_spellings(string path, string prefix, bool expected)
    {
        Assert.Equal(expected, PhaseGateState.Under(path, prefix));
        Assert.Equal(expected, PhaseGateState.Under(path.TrimStart('/'), prefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_route_text_stays_outside_every_prefix(string? path)
    {
        Assert.False(PhaseGateState.Under(path!, Prefix));
        Assert.False(PhaseGateState.Under(path!, VersionedRoot));
        Assert.False(PhaseGateState.Under(path!, "/health"));
    }

    [Theory]
    [MemberData(nameof(SurfaceMatrix))]
    public void Surface_classification_is_identical_for_both_leading_slash_spellings(
        ManagementSurface surface, string path, bool expected)
    {
        Assert.Equal(expected, PhaseGateState.Matches(path, VersionedRoot, surface));
        Assert.Equal(expected, PhaseGateState.Matches(path.TrimStart('/'), VersionedRoot, surface));
    }

    [Fact]
    public async Task Management_api_v1_host_with_attribute_routed_controllers_starts_and_keeps_its_group_protected()
    {
        await using var app = BuildManagementApiHost();
        await app.StartAsync(Token);
        using var client = app.GetTestClient();
        using var business = await client.GetAsync("/api/admin/session/login", Token);
        using var group = await client.GetAsync(VersionedRoot + "/settings", Token);
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
        Assert.Equal("login", await business.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, group.StatusCode);
    }

    private static WebApplication BuildGateHost(Type? controller, ServiceHealthSnapshot? snapshot = null, bool mapGroup = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddServiceMantlePhaseGate();
        if (snapshot is not null) builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new SnapshotSource(snapshot));
        AddControllers(builder.Services, controller);
        var app = builder.Build();
        app.UseRouting();
        app.UseServiceMantlePhaseGate();
        if (mapGroup)
        {
            app.MapServiceMantleManagementGroup()
                .MapGet("/status", () => "status")
                .WithServiceMantleManagementSurface(ManagementSurface.Status);
        }

        app.MapControllers();
        return app;
    }

    private static WebApplication BuildManagementApiHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0")
            .AddSecurityResponseHeaders()
            .AddSensitiveHeaders()
            .AddRateLimiting()
            .AddManagementCookieAuthentication()
            .AddServiceMantleManagementApiV1();
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new SnapshotSource(Ready));
        AddControllers(builder.Services, typeof(BusinessSessionController));
        var app = builder.Build();
        app.UseServiceMantlePipeline();
        app.MapServiceMantleManagementApiV1().MapGet("/settings", () => "settings");
        app.MapControllers();
        return app;
    }

    /// <summary>Limits MVC discovery to the controller one host scenario owns.</summary>
    private static void AddControllers(IServiceCollection services, Type? controller) =>
        services.AddControllers(options => options.Conventions.Add(new SingleController(controller)));

    private static bool ExpectedSurfaceMatch(ManagementSurface surface, string path) => surface switch
    {
        ManagementSurface.Status => path is "/management/v1/status" or "/management/v1/status/deep",
        ManagementSurface.Bootstrap => path is "/management/v1/bootstrap" or "/management/v1/bootstrap/deep",
        ManagementSurface.Setup => path is "/management/v1/setup" or "/management/v1/setup/deep",
        ManagementSurface.Management => path is "/management/v1" or "/management/v1/settings",
        _ => false,
    };

    private sealed class SingleController(Type? controller) : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            foreach (var model in application.Controllers
                .Where(candidate => candidate.ControllerType.AsType() != controller)
                .ToArray())
            {
                application.Controllers.Remove(model);
            }
        }
    }

    private sealed class SnapshotSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }
}

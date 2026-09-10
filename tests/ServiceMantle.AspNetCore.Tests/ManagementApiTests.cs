using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.AspNetCore.RateLimiting;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the protected management API v1 group: its fixed root, its startup-validated baseline,
/// the phase, authentication and rate-limit order in front of it, and the two fixed error results.
/// </summary>
public sealed class ManagementApiTests
{
    private const string Root = "/management/v1";

    private static readonly Dictionary<string, string> Guards = new()
    {
        ["api"] = "The ServiceMantle management API v1 configuration or endpoint mapping is invalid.",
        ["capability"] = "The ServiceMantle management API v1 requires the composed ServiceMantle pipeline,",
        ["gate"] = "The ServiceMantle phase gate configuration or endpoint mapping is invalid.",
        ["pipeline"] = "The ServiceMantle pipeline requires all HTTP capabilities to be registered.",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> Matrix =>
        from phase in Enum.GetValues<ServiceStartupPhase>()
        from migration in Enum.GetValues<ServiceMigrationReadinessState>()
        from database in Enum.GetValues<ServiceDatabaseReadinessState>()
        select new object[] { phase, migration, database };

    [Theory]
    [InlineData(null, "/management/v1/ok", "/management/ok")]
    [InlineData("/ops/admin/v1", "/ops/admin/v1/ok", "/management/v1/ok")]
    public async Task Default_and_custom_roots_map_exactly_one_protected_group(
        string? root, string mapped, string unmapped)
    {
        await using var host = await ManagementApiHostFixture.StartAsync(root: root);
        using var allowed = await host.SendAsync(mapped, host.Cookie("admin", ManagementPermission.Admin));
        using var missing = await host.SendAsync(unmapped, host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1, host.Recorder.Calls);
    }

    [Fact]
    public async Task Registration_without_mapping_starts_and_exposes_no_endpoint()
    {
        await using var host = await ManagementApiHostFixture.CreateAsync(mapCount: 0);
        await host.StartAsync();
        using var response = await host.SendAsync(Root + "/ok", host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Recorder.Calls);
    }

    [Fact]
    public async Task Existing_phase_gate_entry_point_keeps_its_own_default_root()
    {
        Assert.Equal("/management", new PhaseGateOptions().ManagementPathPrefix);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddServiceMantlePhaseGate();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseServiceMantlePhaseGate();
        app.MapServiceMantleManagementGroup()
            .MapGet("/status", () => "status")
            .WithServiceMantleManagementSurface(ManagementSurface.Status);
        await app.StartAsync(Token);
        using var response = await app.GetTestClient().GetAsync("/management/status", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_equivalent_registration_is_idempotent()
    {
        await using var host = await ManagementApiHostFixture.CreateAsync(repeatRegistration: true);
        await host.StartAsync();
        using var response = await host.SendAsync(Root + "/ok", host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("root-not-versioned", "/management/v2", "api")]
    [InlineData("root-invalid-characters", "/管理/v1", "api")]
    [InlineData("root-conflicts-with-health", "/health/v1", "api")]
    [InlineData("root-empty", "", "api")]
    [InlineData("conflicting-registration", "/other/v1", "api")]
    [InlineData("conflicting-gate-root", "/other/v1", "gate")]
    [InlineData("conflicting-gate-timeout", null, "gate")]
    [InlineData("duplicate-mapping", null, "api")]
    [InlineData("without-composed-pipeline", null, "capability")]
    [InlineData("without-authentication", null, "capability")]
    [InlineData("without-default-schemes", null, "capability")]
    [InlineData("without-security-headers", null, "pipeline")]
    [InlineData("without-rate-limiting", null, "pipeline")]
    [InlineData("anonymous-group", null, "api")]
    [InlineData("anonymous-endpoint", null, "api")]
    [InlineData("disabled-limiter-endpoint", null, "api")]
    [InlineData("replaced-limiter-endpoint", null, "api")]
    [InlineData("replaced-limiter-group", null, "api")]
    [InlineData("removed-authorization", null, "api")]
    [InlineData("removed-security-headers", null, "api")]
    [InlineData("second-surface", null, "gate")]
    [InlineData("reserved-status", null, "gate")]
    [InlineData("reserved-bootstrap", null, "gate")]
    [InlineData("reserved-setup", null, "gate")]
    public async Task Invalid_configuration_or_weakened_baseline_fails_before_a_successful_start(
        string scenario, string? value, string guard)
    {
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await using var host = await CreateAsync(scenario, value);
            await host.StartAsync();
        });
        Assert.StartsWith(Guards[guard], error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagementApiHostFixture.Secret, error.ToString(), StringComparison.Ordinal);
        if (value is { Length: > 0 })
        {
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Only_the_completed_succeeded_reachable_state_reaches_authorization(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var source = new ManagementApiHostFixture.SnapshotSource(new(phase, migration, database));
        await using var host = await ManagementApiHostFixture.StartAsync(source);
        var ready = phase == ServiceStartupPhase.Completed &&
            migration == ServiceMigrationReadinessState.Succeeded &&
            database == ServiceDatabaseReadinessState.Reachable;

        using var response = await host.SendAsync(Root + "/ok", host.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ready ? 1 : 0, host.Recorder.Calls);
        Assert.Equal(1, source.Calls);
        ManagementApiHostFixture.AssertSecurityHeaders(response);
        if (!ready)
        {
            Assert.Equal(
                "{\"errorCode\":\"service.phase.unavailable\"}",
                await response.Content.ReadAsStringAsync(Token));
        }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("internal-cancel")]
    [InlineData("timeout")]
    public async Task Missing_failing_or_timing_out_snapshot_sources_fail_closed(string mode)
    {
        await using var host = await ManagementApiHostFixture.CreateAsync(
            source: mode == "absent" ? null : new FailureSource(mode),
            registerSource: mode != "absent",
            snapshotTimeout: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();
        using var response = await host.SendAsync(Root + "/ok", host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"service.phase.unavailable\"}",
            await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, host.Recorder.Calls);
    }

    [Theory]
    [InlineData("none", 401, "management.session.unauthenticated")]
    [InlineData("corrupted", 401, "management.session.expired")]
    [InlineData("expired", 401, "management.session.expired")]
    [InlineData("read-only", 403, "management.session.forbidden")]
    [InlineData("invalid-claims", 403, "management.session.forbidden")]
    [InlineData("admin", 200, null)]
    public async Task Session_and_permission_outcomes_are_fixed_on_an_admitted_request(
        string session, int status, string? errorCode)
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(Root + "/ok", Session(host, session));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, host.Recorder.Calls);
        ManagementApiHostFixture.AssertSecurityHeaders(response);
        if (errorCode is null) return;
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "{\"errorCode\":\"" + errorCode + "\"}",
            await response.Content.ReadAsStringAsync(Token));
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Admin_requests_reach_both_success_shapes()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        var cookie = host.Cookie("admin", ManagementPermission.Admin);
        using var ok = await host.SendAsync(Root + "/ok", cookie);
        using var noContent = await host.SendAsync(Root + "/no-content", cookie);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, noContent.StatusCode);
        Assert.Equal(2, host.Recorder.Calls);
    }

    [Fact]
    public async Task The_gate_precedes_authentication()
    {
        var source = new ManagementApiHostFixture.SnapshotSource(new(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable));
        await using var host = await ManagementApiHostFixture.StartAsync(source);
        using var response = await host.SendAsync(Root + "/ok");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, host.Recorder.Calls);
    }

    [Fact]
    public async Task The_limiter_precedes_authorization_and_exhausted_quota_answers_first()
    {
        await using var host = await ManagementApiHostFixture.StartAsync(managementPermitLimit: 1);
        // Both requests are unauthenticated, so both fall in the same client partition: the limiter
        // answers the second one before authorization can produce its own challenge.
        using var first = await host.SendAsync(Root + "/ok");
        using var second = await host.SendAsync(Root + "/ok");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(0, host.Recorder.Calls);
    }

    [Theory]
    [InlineData("/invalid", 400, "management.request.invalid", "The request is invalid.")]
    [InlineData("/conflict", 409, "management.request.conflict", "The request conflicts with the current state.")]
    public async Task The_fixed_results_carry_exactly_five_public_fields(
        string path, int status, string errorCode, string title)
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(Root + path, host.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(
            new[] { "type", "title", "status", "correlationId", "errorCode" },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            ProblemDetailsDefaults.TypeUriPrefix + errorCode,
            json.RootElement.GetProperty("type").GetString());
        Assert.Equal(title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(errorCode, json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            json.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        ManagementApiHostFixture.AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/ok", "admin", 200)]
    [InlineData("/no-content", "admin", 204)]
    [InlineData("/invalid", "admin", 400)]
    [InlineData("/ok", "none", 401)]
    [InlineData("/ok", "read-only", 403)]
    [InlineData("/conflict", "admin", 409)]
    [InlineData("/exception", "admin", 500)]
    [InlineData("/internal-cancel", "admin", 500)]
    public async Task Every_answered_status_carries_the_shared_response_baseline(
        string path, string session, int status)
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(Root + path, Session(host, session));
        Assert.Equal(status, (int)response.StatusCode);
        ManagementApiHostFixture.AssertSecurityHeaders(response);
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
    }

    [Fact]
    public async Task Rate_limited_and_gate_rejected_responses_keep_the_shared_response_baseline()
    {
        await using var limited = await ManagementApiHostFixture.StartAsync(managementPermitLimit: 1);
        using var accepted = await limited.SendAsync(Root + "/ok", limited.Cookie("a", ManagementPermission.Admin));
        using var rejected = await limited.SendAsync(Root + "/ok", limited.Cookie("a", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(Token));
        Assert.Equal(
            RateLimitingDefaults.RejectedErrorCode,
            problem.RootElement.GetProperty("errorCode").GetString());
        ManagementApiHostFixture.AssertSecurityHeaders(rejected);

        var source = new ManagementApiHostFixture.SnapshotSource(new(
            ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Unreachable));
        await using var gated = await ManagementApiHostFixture.StartAsync(source);
        using var unavailable = await gated.SendAsync(Root + "/ok", gated.Cookie("a", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        ManagementApiHostFixture.AssertSecurityHeaders(unavailable);
        Assert.Equal(
            ManagementApiHostFixture.CorrelationId,
            Assert.Single(unavailable.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
    }

    [Fact]
    public async Task An_unmapped_exception_keeps_the_existing_internal_error_shape()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(Root + "/exception", host.Cookie("a", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(
            ProblemDetailsDefaults.InternalServerErrorCode,
            json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            ProblemDetailsDefaults.InternalServerErrorTitle,
            json.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/invalid", "admin", "GET")]
    [InlineData("/invalid", "admin", "POST")]
    [InlineData("/conflict", "admin", "POST")]
    [InlineData("/exception", "admin", "POST")]
    [InlineData("/internal-cancel", "admin", "POST")]
    [InlineData("/ok", "none", "POST")]
    [InlineData("/ok", "read-only", "POST")]
    public async Task Fixed_results_never_echo_request_or_exception_secrets(
        string path, string session, string method)
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(
            Root + path,
            Session(host, session),
            method: new HttpMethod(method));
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(ManagementApiHostFixture.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagementApiHostFixture.Secret, Headers(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a valid id", null)]
    [InlineData("valid-id", "second-id")]
    [InlineData("", null)]
    public async Task Rejected_correlation_headers_are_replaced_and_never_echoed(string value, string? extra)
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(
            Root + "/invalid",
            host.Cookie("admin", ManagementPermission.Admin),
            correlationId: value.Length == 0 ? null : value,
            extraCorrelationIds: extra is null ? null : [extra]);

        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        var correlationId = json.RootElement.GetProperty("correlationId").GetString();
        Assert.Equal(32, correlationId?.Length);
        Assert.Equal(
            correlationId,
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        if (value.Length > 0) Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        if (extra is not null) Assert.DoesNotContain(extra, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/invalid")]
    [InlineData("/conflict")]
    [InlineData("/exception")]
    public async Task Development_and_production_answer_the_same_body(string path)
    {
        await using var development = await ManagementApiHostFixture.StartAsync(environment: "Development");
        await using var production = await ManagementApiHostFixture.StartAsync(environment: "Production");
        using var first = await development.SendAsync(
            Root + path,
            development.Cookie("admin", ManagementPermission.Admin));
        using var second = await production.SendAsync(
            Root + path,
            production.Cookie("admin", ManagementPermission.Admin));

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Token),
            await second.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_started_response_is_left_unchanged()
    {
        await using var host = await ManagementApiHostFixture.StartAsync();
        using var response = await host.SendAsync(
            Root + "/started",
            host.Cookie("admin", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("already-started", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Endpoints_mapped_outside_the_group_are_not_taken_over()
    {
        await using var host = await ManagementApiHostFixture.StartAsync(outside: application =>
        {
            application.MapServiceMantleManagementGroup()
                .MapGet("/status", () => "status")
                .WithServiceMantleManagementSurface(ManagementSurface.Status);
            application.MapGet("/business", () => "business");
        });

        using var status = await host.SendAsync(Root + "/status");
        using var business = await host.SendAsync("/business");
        using var protectedEndpoint = await host.SendAsync(Root + "/ok");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedEndpoint.StatusCode);
    }

    private static string? Session(ManagementApiHostFixture host, string session) => session switch
    {
        "none" => null,
        "corrupted" => ManagementApiHostFixture.CorruptedCookie(),
        "expired" => host.Cookie("admin", ManagementPermission.Admin, TimeSpan.FromMinutes(30)),
        "read-only" => host.Cookie("reader", ManagementPermission.Read),
        "invalid-claims" => host.CookieWithInvalidClaims(),
        _ => host.Cookie("admin", ManagementPermission.Admin)
    };

    private static string Headers(HttpResponseMessage response) =>
        string.Join(";", response.Headers.Concat(response.Content.Headers)
            .Select(header => header.Key + "=" + string.Join(",", header.Value)));

    private static Task<ManagementApiHostFixture> CreateAsync(string scenario, string? value) => scenario switch
    {
        "root-not-versioned" or "root-invalid-characters" or "root-conflicts-with-health" or "root-empty" =>
            ManagementApiHostFixture.CreateAsync(root: value),
        "conflicting-registration" => ManagementApiHostFixture.CreateAsync(conflictingRoot: value),
        "conflicting-gate-root" => ManagementApiHostFixture.CreateAsync(explicitGateRoot: value),
        "conflicting-gate-timeout" => ManagementApiHostFixture.CreateAsync(
            explicitGateTimeout: TimeSpan.FromSeconds(7)),
        "duplicate-mapping" => ManagementApiHostFixture.CreateAsync(mapCount: 2),
        "without-composed-pipeline" => ManagementApiHostFixture.CreateAsync(composition: "manual"),
        "without-authentication" => ManagementApiHostFixture.CreateAsync(authentication: "none"),
        "without-default-schemes" => ManagementApiHostFixture.CreateAsync(authentication: "schemeless"),
        "without-security-headers" => ManagementApiHostFixture.CreateAsync(securityHeaders: false),
        "without-rate-limiting" => ManagementApiHostFixture.CreateAsync(rateLimiting: false),
        "anonymous-group" => ManagementApiHostFixture.CreateAsync(children: group => group.AllowAnonymous()),
        "anonymous-endpoint" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/open", () => "open").AllowAnonymous()),
        "disabled-limiter-endpoint" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/free", () => "free").DisableRateLimiting()),
        "replaced-limiter-endpoint" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/other", () => "other")
                .RequireRateLimiting(RateLimitingDefaults.SetupPolicyName)),
        "replaced-limiter-group" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.RequireRateLimiting(RateLimitingDefaults.SetupPolicyName)),
        "removed-authorization" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/unprotected", () => "unprotected").Add(endpoint =>
            {
                foreach (var data in endpoint.Metadata.OfType<IAuthorizeData>().ToArray())
                {
                    endpoint.Metadata.Remove(data);
                }
            })),
        "removed-security-headers" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/bare", () => "bare").Add(endpoint =>
            {
                foreach (var marker in endpoint.Metadata
                    .OfType<SecurityResponseHeadersMetadata>().ToArray())
                {
                    endpoint.Metadata.Remove(marker);
                }
            })),
        "second-surface" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/dual", () => "dual")
                .WithServiceMantleManagementSurface(ManagementSurface.Status)),
        "reserved-status" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/status/details", () => "status")),
        "reserved-bootstrap" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/bootstrap", () => "bootstrap")),
        "reserved-setup" => ManagementApiHostFixture.CreateAsync(children: group =>
            group.MapGet("/setup", () => "setup")),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private sealed class FailureSource(string mode) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            mode switch
            {
                "null" => ValueTask.FromResult<ServiceHealthSnapshot>(null!),
                "internal-cancel" => throw new OperationCanceledException(ManagementApiHostFixture.Secret),
                "timeout" => new(new TaskCompletionSource<ServiceHealthSnapshot>().Task),
                _ => throw new InvalidOperationException(ManagementApiHostFixture.Secret)
            };
    }
}

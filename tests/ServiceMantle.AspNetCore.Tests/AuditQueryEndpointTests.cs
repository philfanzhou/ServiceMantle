using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class AuditQueryEndpointTests
{
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "/management/v1/audit")]
    [InlineData("/ops/v1", "/ops/v1/audit")]
    public async Task Audit_query_is_opt_in_under_the_configured_v1_root(string? root, string expectedPath)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service, root: root);
        using var response = await host.SendAsync(cookie: host.AdminCookie());

        Assert.Equal(expectedPath, host.Root + AuditQueryHostFixture.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Baseline_without_opt_in_maps_no_audit_endpoint()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.CreateAsync(service, mapCount: 0);
        await host.StartAsync();
        using var response = await host.SendAsync(cookie: host.AdminCookie());

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Non_get_methods_never_reach_the_query_service()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie(), method: HttpMethod.Post);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("look-alike")]
    [InlineData("nested")]
    [InlineData("missing-service")]
    public async Task Invalid_mapping_or_missing_registration_fails_before_the_host_starts(string scenario)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAndStartInvalidAsync(scenario));

        Assert.DoesNotContain(AuditQueryHostFixture.Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mapping_checks_a_scoped_registration_without_instantiating_it_from_the_root()
    {
        var factoryCalls = 0;
        await using var host = await AuditQueryHostFixture.StartAsync(
            queryFactory: _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new AuditQueryHostFixture.RecordingQueryService();
            });

        Assert.Equal(0, factoryCalls);
        using var response = await host.SendAsync(cookie: host.AdminCookie());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task Defaults_are_forwarded_to_exactly_one_scoped_query_call()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie());

        var query = Assert.Single(service.Queries);
        Assert.Null(query.Action);
        Assert.Null(query.TargetType);
        Assert.Null(query.TargetId);
        Assert.Null(query.OperatorId);
        Assert.Null(query.FromUtc);
        Assert.Null(query.ToUtc);
        Assert.Equal(1, query.Page);
        Assert.Equal(ManagementAuditQuery.DefaultPageSize, query.PageSize);
        Assert.Equal(ManagementAuditSortOrder.Newest, query.SortOrder);
        Assert.Null(query.Cursor);
        Assert.Equal(1, service.Calls);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Every_filter_and_boundary_is_validated_normalized_and_forwarded()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        const string queryString =
            "?action=CONFIGURATION.CHANGED%20&targetType=CONFIGURATION%20&targetId=%20smtp%20" +
            "&operatorId=%20admin-1%20&fromUtc=2026-01-01T00%3A00%3A00%2B08%3A00" +
            "&toUtc=2026-01-02T00%3A00%3A00.1234567Z&page=2147483647&pageSize=200" +
            "&sortOrder=oldest&cursor=opaque-cursor";

        using var response = await host.SendAsync(cookie: host.AdminCookie(), query: queryString);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(service.Queries);
        Assert.Equal("configuration.changed", query.Action?.Value);
        Assert.Equal("configuration", query.TargetType?.Value);
        Assert.Equal("smtp", query.TargetId);
        Assert.Equal("admin-1", query.OperatorId);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 16, 0, 0, TimeSpan.Zero), query.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), query.ToUtc);
        Assert.Equal(int.MaxValue, query.Page);
        Assert.Equal(200, query.PageSize);
        Assert.Equal(ManagementAuditSortOrder.Oldest, query.SortOrder);
        Assert.Equal("opaque-cursor", query.Cursor);
    }

    [Theory]
    [InlineData("?unknown=x")]
    [InlineData("?token=" + AuditQueryHostFixture.Secret)]
    [InlineData("?Action=configuration.changed")]
    [InlineData("?action=a&action=b")]
    [InlineData("?action=a&ACTION=b")]
    [InlineData("?action=")]
    [InlineData("?targetType=")]
    [InlineData("?targetId=")]
    [InlineData("?operatorId=")]
    [InlineData("?fromUtc=")]
    [InlineData("?toUtc=")]
    [InlineData("?page=")]
    [InlineData("?pageSize=")]
    [InlineData("?sortOrder=")]
    [InlineData("?cursor=")]
    [InlineData("?page=0")]
    [InlineData("?page=+1")]
    [InlineData("?page=-1")]
    [InlineData("?page=2147483648")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=201")]
    [InlineData("?sortOrder=Newest")]
    [InlineData("?sortOrder=descending")]
    [InlineData("?cursor=first-page-cursor")]
    [InlineData("?page=2")]
    [InlineData("?action=not%2Fvalid")]
    [InlineData("?targetType=not%2Fvalid")]
    [InlineData("?targetId=%20")]
    [InlineData("?operatorId=%20")]
    public async Task Invalid_unknown_repeated_empty_or_out_of_range_input_never_calls_the_service(string query)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie(), query: query);

        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ManagementApiDefaults.InvalidRequestErrorCode, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AuditQueryHostFixture.Secret, body, StringComparison.Ordinal);
        Assert.Equal(0, service.Calls);
        AuditQueryHostFixture.AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("?fromUtc=2026-01-01")]
    [InlineData("?fromUtc=2026-01-01T00%3A00%3A00")]
    [InlineData("?fromUtc=2026-01-01T00%3A00Z")]
    [InlineData("?fromUtc=2026-01-01T00%3A00%3A00.Z")]
    [InlineData("?fromUtc=2026-01-01T00%3A00%3A00.12345678Z")]
    [InlineData("?fromUtc=2026-01-01T00%3A00%3A00%2B0800")]
    [InlineData("?fromUtc=2026-01-02T00%3A00%3A00Z&toUtc=2026-01-01T00%3A00%3A00Z")]
    [InlineData("?fromUtc=2025-01-01T00%3A00%3A00Z&toUtc=2026-01-03T00%3A00%3A00Z")]
    public async Task Invalid_or_overwide_time_ranges_are_rejected_before_query(string query)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie(), query: query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Raw_filter_lengths_and_request_bodies_are_rejected_before_normalization()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        var overlong = new string('a', ManagementAuditAction.MaxLength + 1);
        using var lengthResponse = await host.SendAsync(
            cookie: host.AdminCookie(),
            query: "?action=" + overlong);
        using var bodyResponse = await host.SendAsync(
            cookie: host.AdminCookie(),
            content: new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, lengthResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bodyResponse.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("action", ManagementAuditAction.MaxLength)]
    [InlineData("targetType", ManagementAuditTargetType.MaxLength)]
    [InlineData("targetId", ManagementAuditTarget.MaxTargetIdLength)]
    [InlineData("operatorId", ManagementAuditOperator.MaxOperatorIdLength)]
    [InlineData("cursor", ManagementAuditQuery.MaxCursorLength)]
    public async Task Every_text_input_is_rejected_at_one_character_beyond_its_raw_limit(
        string name,
        int maximumLength)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        var paging = name == "cursor" ? "&page=2" : "";
        using var response = await host.SendAsync(
            cookie: host.AdminCookie(),
            query: "?" + name + "=" + new string('a', maximumLength + 1) + paging);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Maximum_length_text_inputs_are_accepted_before_core_normalization()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(
            cookie: host.AdminCookie(),
            query: "?action=" + new string('a', ManagementAuditAction.MaxLength)
                + "&targetType=" + new string('b', ManagementAuditTargetType.MaxLength)
                + "&targetId=" + new string('c', ManagementAuditTarget.MaxTargetIdLength)
                + "&operatorId=" + new string('d', ManagementAuditOperator.MaxOperatorIdLength)
                + "&page=2&cursor=" + new string('e', ManagementAuditQuery.MaxCursorLength));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(service.Queries);
        Assert.Equal(ManagementAuditAction.MaxLength, query.Action?.Value.Length);
        Assert.Equal(ManagementAuditTargetType.MaxLength, query.TargetType?.Value.Length);
        Assert.Equal(ManagementAuditTarget.MaxTargetIdLength, query.TargetId?.Length);
        Assert.Equal(ManagementAuditOperator.MaxOperatorIdLength, query.OperatorId?.Length);
        Assert.Equal(ManagementAuditQuery.MaxCursorLength, query.Cursor?.Length);
    }

    [Fact]
    public async Task Success_serializes_only_the_closed_public_shape_and_explicit_nulls()
    {
        var id = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = (query, _) => ValueTask.FromResult(new ManagementAuditQueryResult(
                [Record(id, ManagementAuditOutcome.Denied)],
                query.Page,
                query.PageSize,
                2,
                "next-cursor"))
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie());
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "continuationCursor", "hasNextPage"],
            Names(json.RootElement));
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(
            ["id", "operator", "action", "target", "outcome", "occurredAtUtc", "clientIp",
                "correlationId", "securityDescription", "metadata"],
            Names(item));
        Assert.Equal(id.ToString("D"), item.GetProperty("id").GetString());
        Assert.Equal("denied", item.GetProperty("outcome").GetString());
        Assert.Equal(
            TimeSpan.Zero,
            DateTimeOffset.Parse(
                item.GetProperty("occurredAtUtc").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture).Offset);
        Assert.Equal(["operatorId", "displayName", "source"], Names(item.GetProperty("operator")));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("operator").GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("clientIp").ValueKind);
        Assert.Equal("safe", item.GetProperty("metadata").GetProperty("a").GetString());
        Assert.DoesNotContain("metadata_json", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData(ManagementAuditOutcome.Unknown, "unknown")]
    [InlineData(ManagementAuditOutcome.Success, "success")]
    [InlineData(ManagementAuditOutcome.Failure, "failure")]
    [InlineData(ManagementAuditOutcome.Denied, "denied")]
    public async Task Every_defined_outcome_has_one_wire_value(ManagementAuditOutcome outcome, string expected)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = (query, _) => ValueTask.FromResult(new ManagementAuditQueryResult(
                [Record(Guid.NewGuid(), outcome)], query.Page, query.PageSize, 1))
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        using var response = await host.SendAsync(cookie: host.AdminCookie());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(expected, json.RootElement.GetProperty("items")[0].GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("query")]
    [InlineData("entity")]
    [InlineData("exception")]
    [InlineData("internal-cancel")]
    public async Task Query_failures_map_to_closed_400_or_503_without_internal_text(string scenario)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = (_, _) => scenario switch
            {
                "query" => ValueTask.FromException<ManagementAuditQueryResult>(
                    new ManagementAuditException("audit.query_cursor_invalid", AuditQueryHostFixture.Secret)),
                "entity" => ValueTask.FromException<ManagementAuditQueryResult>(
                    new ManagementAuditException("audit.entity_invalid", AuditQueryHostFixture.Secret)),
                "exception" => ValueTask.FromException<ManagementAuditQueryResult>(
                    new InvalidOperationException(AuditQueryHostFixture.Secret)),
                _ => ValueTask.FromException<ManagementAuditQueryResult>(
                    new OperationCanceledException(AuditQueryHostFixture.Secret))
            }
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service, environment: "Development");
        using var response = await host.SendAsync(cookie: host.AdminCookie());
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(scenario == "query" ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable,
            response.StatusCode);
        Assert.Contains(
            scenario == "query"
                ? ManagementApiDefaults.InvalidRequestErrorCode
                : "management.audit.unavailable",
            body,
            StringComparison.Ordinal);
        if (scenario != "query")
        {
            Assert.Equal("{\"errorCode\":\"management.audit.unavailable\"}", body);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.DoesNotContain(AuditQueryHostFixture.Secret, body, StringComparison.Ordinal);
        Assert.Equal(1, service.Calls);
        AuditQueryHostFixture.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_never_returns_an_error_body()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        var abort = host.CreateAbort();
        var request = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, abort.Token, "cancelled-audit-request"),
            Token));
        await entered.Task.WaitAsync(Observation, Token);
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.True(service.ObservedToken.IsCancellationRequested);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_share_cancellation_or_correlation_state()
    {
        var cancelledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = async (query, cancellationToken) =>
            {
                if (query.OperatorId == "cancel")
                {
                    cancelledEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                await Task.Delay(50, cancellationToken);
                return new ManagementAuditQueryResult([], query.Page, query.PageSize, 0);
            }
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service);
        var abort = host.CreateAbort();
        var cancelled = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(
                context,
                host,
                abort.Token,
                "cancelled-correlation",
                "?operatorId=cancel"),
            Token));
        await cancelledEntered.Task.WaitAsync(Observation, Token);
        var successful = host.Track(host.SendAsync(
            cookie: host.AdminCookie(),
            query: "?operatorId=ok",
            correlationId: "successful-correlation"));
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        using var response = await successful.WaitAsync(Observation, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "successful-correlation",
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        Assert.Equal(2, service.Calls);
    }

    [Theory]
    [InlineData("gate", 503)]
    [InlineData("none", 401)]
    [InlineData("corrupted", 401)]
    [InlineData("expired", 401)]
    [InlineData("read-only", 403)]
    [InlineData("invalid-claims", 403)]
    public async Task Gate_and_authorization_rejections_have_no_query_side_effect(string session, int status)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(
            service,
            health: session == "gate"
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.PendingSetup,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Reachable)
                : null);
        using var response = await host.SendAsync(cookie: Session(host, session));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, service.Calls);
        AuditQueryHostFixture.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Rate_limit_rejection_occurs_before_a_second_query()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        await using var host = await AuditQueryHostFixture.StartAsync(service, managementPermitLimit: 1);
        var cookie = host.AdminCookie();
        using var accepted = await host.SendAsync(cookie: cookie);
        using var rejected = await host.SendAsync(cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Development_and_production_answer_the_same_fixed_failure()
    {
        static AuditQueryHostFixture.RecordingQueryService Failing() => new()
        {
            Handler = (_, _) => ValueTask.FromException<ManagementAuditQueryResult>(
                new InvalidOperationException(AuditQueryHostFixture.Secret))
        };

        await using var development = await AuditQueryHostFixture.StartAsync(Failing(), environment: "Development");
        await using var production = await AuditQueryHostFixture.StartAsync(Failing(), environment: "Production");
        using var first = await development.SendAsync(cookie: development.AdminCookie());
        using var second = await production.SendAsync(cookie: production.AdminCookie());

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Token),
            await second.Content.ReadAsStringAsync(Token));
    }

    private static async Task CreateAndStartInvalidAsync(string scenario)
    {
        var service = new AuditQueryHostFixture.RecordingQueryService();
        var fixture = scenario switch
        {
            "duplicate" => await AuditQueryHostFixture.CreateAsync(service, mapCount: 2),
            "outside" => await AuditQueryHostFixture.CreateAsync(
                service, mapCount: 0,
                outside: application => application.MapServiceMantleAuditQueries()),
            "look-alike" => await AuditQueryHostFixture.CreateAsync(
                service, mapCount: 0,
                outside: application => application.MapGroup("/other/v1").MapServiceMantleAuditQueries()),
            "nested" => await AuditQueryHostFixture.CreateAsync(
                service, mapCount: 0,
                children: group => group.MapGroup("/nested").MapServiceMantleAuditQueries()),
            "missing-service" => await AuditQueryHostFixture.CreateAsync(registerQueryService: false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        await using (fixture)
        {
            await fixture.StartAsync();
        }
    }

    private static ManagementAuditRecord Record(Guid id, ManagementAuditOutcome outcome) =>
        new(
            id,
            ManagementAuditOperator.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                "admin-1"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
            outcome,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            clientIp: null,
            correlationId: "request-1",
            securityDescription: "safe description",
            new Dictionary<string, string> { ["z"] = "last", ["a"] = "safe" });

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static void Configure(
        HttpContext context,
        AuditQueryHostFixture host,
        CancellationToken abort,
        string correlationId,
        string query = "")
    {
        context.Request.Method = "GET";
        context.Request.Path = host.Root + AuditQueryHostFixture.Path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers.Cookie = host.AdminCookie();
        context.Request.Headers[ServiceHeaderNames.CorrelationId] = correlationId;
        context.RequestAborted = abort;
    }

    private static string? Session(AuditQueryHostFixture host, string session) => session switch
    {
        "none" => null,
        "corrupted" => AuditQueryHostFixture.CorruptedCookie(),
        "expired" => host.Cookie("admin", ManagementPermission.Admin, TimeSpan.FromMinutes(30)),
        "read-only" => host.Cookie("reader", ManagementPermission.Read),
        "invalid-claims" => host.CookieWithInvalidClaims(),
        _ => host.AdminCookie()
    };
}

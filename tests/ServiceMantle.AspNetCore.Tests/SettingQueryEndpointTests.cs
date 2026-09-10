using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.RateLimiting;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the read-only management API v1 setting queries: their opt-in mapping, the two fixed
/// projections, the bounded group filter, the single closed refresh rejection, and the protection,
/// cancellation, concurrency and secret-free guarantees they inherit from the group.
/// </summary>
public sealed class SettingQueryEndpointTests
{
    private const string Definitions = SettingQueryHostFixture.DefinitionsPath;
    private const string Current = SettingQueryHostFixture.CurrentValuesPath;
    private const string RootKey = "root-key-with-enough-entropy-for-endpoint-tests";
    private const string Unavailable = "{\"errorCode\":\"management.settings.unavailable\"}";

    private const string MappingGuard =
        "The ServiceMantle management API v1 setting query endpoints must be mapped at most once,";

    private const string CapabilityGuard =
        "The ServiceMantle management API v1 setting query endpoints require the setting";

    private static readonly string[] DefinitionFields =
        ["key", "valueType", "isRequired", "isSensitive", "hasDefault", "requiresRestart"];

    private static readonly string[] ValueFields =
        [.. DefinitionFields, "hasValue", "source", "value"];

    private static readonly string[] CatalogKeys =
    [
        "billing",
        "billing.rate",
        "product.boolean",
        "product.default",
        "product.json",
        "product.missing",
        "product.number",
        "product.secret-boolean",
        "product.secret-json",
        "product.secret-number",
        "product.secret-string",
        "product.string",
    ];

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/admin/v1")]
    public async Task Both_endpoints_follow_the_group_root(string? root)
    {
        await using var host = await StartAsync(root: root);
        var cookie = host.AdminCookie();
        using var definitions = await host.SendAsync(Definitions, cookie);
        using var current = await host.SendAsync(Current, cookie);
        using var unmapped = await host.Client.GetAsync(
            (root is null ? "/ops/admin/v1" : "/management/v1") + Current,
            Token);

        Assert.Equal(HttpStatusCode.OK, definitions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
    }

    [Fact]
    public async Task The_baseline_group_alone_exposes_no_setting_endpoint()
    {
        await using var host = await SettingQueryHostFixture.CreateAsync(
            definitions: Catalog(),
            snapshotSource: new SettingQueryHostFixture.RecordingSource(Snapshot()),
            rootKeySource: new RootKeySource(RootKey),
            mapCount: 0);
        await host.StartAsync();
        var cookie = host.AdminCookie();
        using var definitions = await host.SendAsync(Definitions, cookie);
        using var current = await host.SendAsync(Current, cookie);

        Assert.Equal(HttpStatusCode.NotFound, definitions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
    }

    [Theory]
    [InlineData("duplicate", MappingGuard)]
    [InlineData("outside-the-group", MappingGuard)]
    [InlineData("look-alike-group", MappingGuard)]
    [InlineData("nested-group", MappingGuard)]
    [InlineData("missing-query-service", CapabilityGuard)]
    public async Task A_repeated_misplaced_or_unsupported_mapping_fails_before_a_successful_start(
        string scenario, string guard)
    {
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await using var host = await Create(scenario);
            await host.StartAsync();
        });

        Assert.StartsWith(guard, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingQueryHostFixture.Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Definitions)]
    [InlineData(Current)]
    public async Task Neither_endpoint_answers_a_write_method(string path)
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        using var response = await host.SendAsync(path, host.AdminCookie(), method: HttpMethod.Post);

        // The framework's method rejection is answered by the existing gate baseline, unchanged.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"service.phase.unavailable\"}",
            await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Definitions_project_six_fields_in_ordinal_order_without_defaults()
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        using var response = await host.SendAsync(Definitions, host.AdminCookie());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["definitions"], Names(json.RootElement));
        var definitions = json.RootElement.GetProperty("definitions").EnumerateArray().ToArray();
        Assert.Equal(CatalogKeys, definitions.Select(item => item.GetProperty("key").GetString()));
        Assert.All(definitions, item => Assert.Equal(DefinitionFields, Names(item)));
        var number = Assert.Single(definitions, item => item.GetProperty("key").GetString() == "product.default");
        Assert.Equal("number", number.GetProperty("valueType").GetString());
        Assert.True(number.GetProperty("hasDefault").GetBoolean());
        Assert.True(number.GetProperty("requiresRestart").GetBoolean());
        var secret = Assert.Single(definitions, item => item.GetProperty("key").GetString() == "product.secret-json");
        Assert.Equal("json", secret.GetProperty("valueType").GetString());
        Assert.True(secret.GetProperty("isSensitive").GetBoolean());
        Assert.True(secret.GetProperty("isRequired").GetBoolean());
        // The definition catalog is projected without defaults, constraints, or refreshed state.
        Assert.DoesNotContain("3.00", body, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Current_values_project_one_version_and_nine_fields()
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        using var response = await host.SendAsync(Current, host.AdminCookie());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["version", "values"], Names(json.RootElement));
        Assert.Equal(7, json.RootElement.GetProperty("version").GetInt64());
        var values = json.RootElement.GetProperty("values").EnumerateArray().ToArray();
        Assert.Equal(CatalogKeys, values.Select(item => item.GetProperty("key").GetString()));
        Assert.All(values, item => Assert.Equal(ValueFields, Names(item)));

        AssertValue(values, "product.string", "string", "persisted", "Orders");
        AssertValue(values, "product.number", "number", "persisted", "2.5");
        AssertValue(values, "product.boolean", "boolean", "persisted", "true");
        AssertValue(values, "product.json", "json", "persisted", "{\"mode\":true}");
        AssertValue(values, "product.default", "number", "default", "3");
        AssertMissing(values, "product.missing");
        foreach (var key in CatalogKeys.Where(key => key.StartsWith("product.secret-", StringComparison.Ordinal)))
        {
            var sensitive = Value(values, key);
            Assert.True(sensitive.GetProperty("isSensitive").GetBoolean());
            Assert.True(sensitive.GetProperty("hasValue").GetBoolean());
            Assert.Equal("persisted", sensitive.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, sensitive.GetProperty("value").ValueKind);
        }

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Each_current_request_refreshes_exactly_once_and_definitions_never_do()
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        var cookie = host.AdminCookie();

        using var definitions = await host.SendAsync(Definitions, cookie);
        Assert.Equal(0, source.Calls);
        using var first = await host.SendAsync(Current, cookie);
        Assert.Equal(1, source.Calls);
        using var second = await host.SendAsync(Current, cookie);
        Assert.Equal(2, source.Calls);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Theory]
    [InlineData("billing", "billing", "billing.rate")]
    [InlineData("BILLING ", "billing", "billing.rate")]
    [InlineData("product.secret-json", "product.secret-json", null)]
    [InlineData("bill", null, null)]
    [InlineData("product.secret", null, null)]
    public async Task The_group_filter_matches_an_exact_key_or_a_dot_separated_descendant(
        string group, string? first, string? second)
    {
        var expected = new[] { first, second }.Where(key => key is not null).ToArray();
        await using var host = await StartAsync();
        var cookie = host.AdminCookie();
        var query = "?group=" + Uri.EscapeDataString(group);

        using var definitions = await host.SendAsync(Definitions, cookie, query);
        using var current = await host.SendAsync(Current, cookie, query);
        using var definitionsJson = JsonDocument.Parse(await definitions.Content.ReadAsStringAsync(Token));
        using var currentJson = JsonDocument.Parse(await current.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.OK, definitions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(expected, Keys(definitionsJson.RootElement, "definitions"));
        Assert.Equal(expected, Keys(currentJson.RootElement, "values"));
        // A legitimate group that matches nothing is an empty result of one complete refresh.
        Assert.Equal(7, currentJson.RootElement.GetProperty("version").GetInt64());
    }

    [Theory]
    [InlineData("?group=")]
    [InlineData("?group=%20")]
    [InlineData("?group=.leading")]
    [InlineData("?group=UPPER%2Fslash")]
    [InlineData("?group=a&group=b")]
    [InlineData("?group=billing&extra=1")]
    [InlineData("?token=setting-query-fake-secret")]
    [InlineData("?GROUP=billing")]
    [InlineData("?group=" + LongGroup)]
    public async Task An_invalid_repeated_or_unknown_query_is_rejected_without_an_echo(string query)
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        var cookie = host.AdminCookie();

        foreach (var path in new[] { Definitions, Current })
        {
            using var response = await host.SendAsync(path, cookie, query);
            var body = await response.Content.ReadAsStringAsync(Token);
            using var json = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                ManagementApiDefaults.InvalidRequestErrorCode,
                json.RootElement.GetProperty("errorCode").GetString());
            Assert.DoesNotContain(SettingQueryHostFixture.Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("billing", body, StringComparison.Ordinal);
            SettingQueryHostFixture.AssertSecurityHeaders(response);
        }

        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("mixed-version")]
    [InlineData("type-mismatch")]
    [InlineData("missing-required")]
    [InlineData("envelope-required")]
    [InlineData("corrupt-ciphertext")]
    [InlineData("wrong-root-key")]
    [InlineData("missing-root-key")]
    [InlineData("constraint")]
    [InlineData("composite")]
    [InlineData("storage-exception")]
    [InlineData("null-read")]
    [InlineData("internal-cancel")]
    [InlineData("stale-version")]
    [InlineData("same-version-conflict")]
    public async Task Every_refresh_failure_answers_one_fixed_rejection(string scenario)
    {
        var plan = Plan(scenario);
        var source = new SettingQueryHostFixture.RecordingSource(plan.Initial);
        await using var host = await SettingQueryHostFixture.StartAsync(
            source,
            FailureCatalog(),
            plan.RootKey ? new RootKeySource(RootKey) : null,
            plan.Composite ? new RejectingValidator() : null);
        var cookie = host.AdminCookie();
        if (plan.Prime)
        {
            using var primed = await host.SendAsync(Current, cookie);
            Assert.Equal(HttpStatusCode.OK, primed.StatusCode);
        }

        source.Read = plan.Mutated;
        source.Failure = plan.Failure;

        // A group filter shapes output only; it can never let a failed refresh answer 200.
        foreach (var query in new[] { "", "?group=product" })
        {
            using var response = await host.SendAsync(Current, cookie, query);
            var body = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Unavailable, body);
            Assert.DoesNotContain(SettingQueryHostFixture.Secret, body, StringComparison.Ordinal);
            SettingQueryHostFixture.AssertSecurityHeaders(response);
            Assert.Equal(
                SettingQueryHostFixture.CorrelationId,
                Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        }
    }

    [Fact]
    public async Task Concurrent_current_requests_each_project_one_self_consistent_version()
    {
        var source = new BlockingSource();
        await using var host = await SettingQueryHostFixture.StartAsync(
            source,
            [new ServiceSettingDefinition("product.string", ServiceSettingValueType.String)]);
        var cookie = host.AdminCookie();

        var first = host.Track(host.SendAsync(Current, cookie));
        await source.Entered.Task.WaitAsync(Observation, Token);
        var second = host.Track(host.SendAsync(Current, cookie));
        // The first request holds the loader's refresh lock, so the second cannot read before it.
        await source.WaitForPendingAsync();
        source.Release();

        using var firstResponse = await first.WaitAsync(Observation, Token);
        using var secondResponse = await second.WaitAsync(Observation, Token);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync(Token));
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(Token));

        Assert.Equal(2, source.Calls);
        AssertVersion(firstJson.RootElement, 1);
        AssertVersion(secondJson.RootElement, 2);
    }

    [Fact]
    public async Task An_already_cancelled_request_never_refreshes()
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source);
        var abort = host.CreateAbort();
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Application.GetTestServer().SendAsync(
                context => Configure(context, host, Current, abort.Token, "cancelled-request"),
                Token));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_cancellation_while_another_refresh_holds_the_lock_stays_the_callers()
    {
        var source = new BlockingSource();
        await using var host = await SettingQueryHostFixture.StartAsync(
            source,
            [new ServiceSettingDefinition("product.string", ServiceSettingValueType.String)]);
        var abort = host.CreateAbort();
        var admitted = host.Track(host.SendAsync(Current, host.AdminCookie()));
        await source.Entered.Task.WaitAsync(Observation, Token);
        var gateReads = host.GateReads;

        var blocked = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, Current, abort.Token, "blocked-request"),
            Token));
        await WaitForGateAsync(host, gateReads + 1);
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Equal(abort.Token, error.CancellationToken);

        source.Release();
        using var response = await admitted.WaitAsync(Observation, Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        // The cancelled request never reached the source and never disturbed the admitted one.
        Assert.Equal(1, source.Calls);
        AssertVersion(json.RootElement, 1);
    }

    [Fact]
    public async Task A_cancellation_during_a_cooperative_read_stays_the_callers()
    {
        var source = new BlockingSource();
        await using var host = await SettingQueryHostFixture.StartAsync(
            source,
            [new ServiceSettingDefinition("product.string", ServiceSettingValueType.String)]);
        var abort = host.CreateAbort();

        var request = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, Current, abort.Token, "cancelled-request"),
            Token));
        await source.Entered.Task.WaitAsync(Observation, Token);
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.True(source.ObservedToken.IsCancellationRequested);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(Definitions, "gate")]
    [InlineData(Current, "gate")]
    [InlineData(Definitions, "none")]
    [InlineData(Current, "none")]
    [InlineData(Definitions, "corrupted")]
    [InlineData(Current, "corrupted")]
    [InlineData(Definitions, "expired")]
    [InlineData(Current, "expired")]
    [InlineData(Definitions, "read-only")]
    [InlineData(Current, "read-only")]
    [InlineData(Definitions, "invalid-claims")]
    [InlineData(Current, "invalid-claims")]
    public async Task A_rejected_request_never_reaches_the_setting_query(string path, string session)
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(
            source,
            health: session == "gate"
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.PendingSetup,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Reachable)
                : null);
        using var response = await host.SendAsync(path, Session(host, session));
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(Expected(session), (int)response.StatusCode);
        Assert.Equal("{\"errorCode\":\"" + ErrorCode(session) + "\"}", body);
        SettingQueryHostFixture.AssertSecurityHeaders(response);
        Assert.Equal(
            SettingQueryHostFixture.CorrelationId,
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(Definitions)]
    [InlineData(Current)]
    public async Task An_exhausted_management_quota_answers_before_the_setting_query(string path)
    {
        var source = new SettingQueryHostFixture.RecordingSource(Snapshot());
        await using var host = await StartAsync(source, managementPermitLimit: 1);
        var cookie = host.AdminCookie();
        using var accepted = await host.SendAsync(path, cookie);
        using var rejected = await host.SendAsync(path, cookie);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(Token));
        Assert.Equal(
            RateLimitingDefaults.RejectedErrorCode,
            problem.RootElement.GetProperty("errorCode").GetString());
        SettingQueryHostFixture.AssertSecurityHeaders(rejected);
        Assert.Equal(path == Current ? 1 : 0, source.Calls);
    }

    [Fact]
    public async Task Secrets_never_reach_a_body_a_fixed_error_or_the_endpoint_diagnostics()
    {
        const string plaintext = "sensitive-plaintext-never-projected";
        const string defaultValue = "default-material-only-in-current-values";
        const string constraintSecret = "constraint-internal-secret";
        var ciphertext = SettingQueryHostFixture.Protect("product.token", plaintext, RootKey);
        var source = new SettingQueryHostFixture.RecordingSource(SettingQueryHostFixture.Read(
            3,
            SettingQueryHostFixture.Value("product.token", 3, ServiceSettingValueType.String, ciphertext)));
        await using var host = await SettingQueryHostFixture.StartAsync(
            source,
            [
                new ServiceSettingDefinition("product.token", ServiceSettingValueType.String, isSensitive: true),
                new ServiceSettingDefinition("product.plain", ServiceSettingValueType.String, defaultValue: defaultValue),
                new ServiceSettingDefinition(
                    "product.limit",
                    ServiceSettingValueType.String,
                    constraints: [new SecretCarryingConstraint(constraintSecret)]),
            ],
            new RootKeySource(RootKey));
        var cookie = host.AdminCookie();
        var secrets = new[] { plaintext, ciphertext, RootKey, constraintSecret, SettingQueryHostFixture.Secret };

        using var definitions = await host.SendAsync(Definitions, cookie, secrets: true);
        using var current = await host.SendAsync(Current, cookie, secrets: true);
        using var rejectedQuery = await host.SendAsync(
            Current,
            cookie,
            "?token=" + Uri.EscapeDataString(SettingQueryHostFixture.Secret));
        using var rejectedBody = await host.SendAsync(
            Current, cookie, method: HttpMethod.Post, secrets: true);
        var definitionsBody = await definitions.Content.ReadAsStringAsync(Token);
        var currentBody = await current.Content.ReadAsStringAsync(Token);

        source.Failure = new InvalidOperationException(plaintext + " " + RootKey);
        using var unavailable = await host.SendAsync(Current, cookie);
        var unavailableBody = await unavailable.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, definitions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedQuery.StatusCode);
        Assert.Equal(Unavailable, unavailableBody);
        // The definition catalog never carries the default; the non-sensitive current value does.
        Assert.DoesNotContain(defaultValue, definitionsBody, StringComparison.Ordinal);
        Assert.Contains(defaultValue, currentBody, StringComparison.Ordinal);
        foreach (var body in new[] { definitionsBody, currentBody, unavailableBody,
            await rejectedQuery.Content.ReadAsStringAsync(Token),
            await rejectedBody.Content.ReadAsStringAsync(Token) })
        {
            Assert.All(secrets, secret =>
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal));
        }

        foreach (var response in new[] { definitions, current, rejectedQuery, rejectedBody, unavailable })
        {
            Assert.All(secrets, secret =>
                Assert.DoesNotContain(secret, Headers(response), StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(Definitions)]
    [InlineData(Current)]
    public async Task Development_and_production_answer_the_same_body(string path)
    {
        await using var development = await StartAsync(environment: "Development");
        await using var production = await StartAsync(environment: "Production");
        using var first = await development.SendAsync(path, development.AdminCookie());
        using var second = await production.SendAsync(path, production.AdminCookie());

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Token),
            await second.Content.ReadAsStringAsync(Token));
    }

    private const string LongGroup =
        "aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeeeffffffffffgggggggggghhhhhhhhhh" +
        "iiiiiiiiiijjjjjjjjjjkkkkkkkkkkllllllllllmmmmmmmmmm";

    private static Task<SettingQueryHostFixture> StartAsync(
        SettingQueryHostFixture.RecordingSource? source = null,
        ServiceHealthSnapshot? health = null,
        string? root = null,
        int managementPermitLimit = 120,
        string environment = "Production") =>
        SettingQueryHostFixture.StartAsync(
            source ?? new SettingQueryHostFixture.RecordingSource(Snapshot()),
            Catalog(),
            new RootKeySource(RootKey),
            health: health,
            root: root,
            managementPermitLimit: managementPermitLimit,
            environment: environment);

    private static Task<SettingQueryHostFixture> Create(string scenario) => scenario switch
    {
        "duplicate" => SettingQueryHostFixture.CreateAsync(
            new SettingQueryHostFixture.RecordingSource(Snapshot()), Catalog(),
            new RootKeySource(RootKey), mapCount: 2),
        "outside-the-group" => SettingQueryHostFixture.CreateAsync(
            new SettingQueryHostFixture.RecordingSource(Snapshot()), Catalog(),
            new RootKeySource(RootKey), mapCount: 0,
            outside: application => application.MapServiceMantleSettingQueries()),
        "look-alike-group" => SettingQueryHostFixture.CreateAsync(
            new SettingQueryHostFixture.RecordingSource(Snapshot()), Catalog(),
            new RootKeySource(RootKey), mapCount: 0,
            outside: application => application.MapGroup("/other/v1").MapServiceMantleSettingQueries()),
        "nested-group" => SettingQueryHostFixture.CreateAsync(
            new SettingQueryHostFixture.RecordingSource(Snapshot()), Catalog(),
            new RootKeySource(RootKey), mapCount: 0,
            children: group => group.MapGroup("/nested").MapServiceMantleSettingQueries()),
        "missing-query-service" => SettingQueryHostFixture.CreateAsync(registerSettings: false),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static void Configure(
        HttpContext context,
        SettingQueryHostFixture host,
        string path,
        CancellationToken abort,
        string correlationId)
    {
        context.Request.Method = "GET";
        context.Request.Path = host.Root + path;
        context.Request.Headers.Cookie = host.AdminCookie();
        context.Request.Headers[ServiceHeaderNames.CorrelationId] = correlationId;
        context.RequestAborted = abort;
    }

    private static async Task WaitForGateAsync(SettingQueryHostFixture host, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + Observation;
        while (host.GateReads < expected)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The phase gate was not reached.");
            await Task.Delay(10, Token);
        }
    }

    private static string? Session(SettingQueryHostFixture host, string session) => session switch
    {
        "none" => null,
        "corrupted" => SettingQueryHostFixture.CorruptedCookie(),
        "expired" => host.Cookie("admin", ManagementPermission.Admin, TimeSpan.FromMinutes(30)),
        "read-only" => host.Cookie("reader", ManagementPermission.Read),
        "invalid-claims" => host.CookieWithInvalidClaims(),
        _ => host.AdminCookie()
    };

    private static int Expected(string session) => session switch
    {
        "gate" => 503,
        "none" or "corrupted" or "expired" => 401,
        _ => 403
    };

    private static string ErrorCode(string session) => session switch
    {
        "gate" => "service.phase.unavailable",
        "none" => "management.session.unauthenticated",
        "corrupted" or "expired" => "management.session.expired",
        _ => "management.session.forbidden"
    };

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string?[] Keys(JsonElement root, string arrayName) =>
        root.GetProperty(arrayName).EnumerateArray()
            .Select(item => item.GetProperty("key").GetString())
            .ToArray();

    private static JsonElement Value(JsonElement[] values, string key) =>
        Assert.Single(values, item => item.GetProperty("key").GetString() == key);

    private static void AssertValue(
        JsonElement[] values, string key, string valueType, string source, string value)
    {
        var item = Value(values, key);
        Assert.Equal(valueType, item.GetProperty("valueType").GetString());
        Assert.Equal(source, item.GetProperty("source").GetString());
        Assert.True(item.GetProperty("hasValue").GetBoolean());
        Assert.Equal(value, item.GetProperty("value").GetString());
    }

    private static void AssertMissing(JsonElement[] values, string key)
    {
        var item = Value(values, key);
        Assert.False(item.GetProperty("hasValue").GetBoolean());
        Assert.Equal("missing", item.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("value").ValueKind);
    }

    private static void AssertVersion(JsonElement root, long version)
    {
        Assert.Equal(version, root.GetProperty("version").GetInt64());
        var value = Assert.Single(root.GetProperty("values").EnumerateArray().ToArray());
        Assert.Equal("version-" + version, value.GetProperty("value").GetString());
    }

    private static string Headers(HttpResponseMessage response) =>
        string.Join(";", response.Headers.Concat(response.Content.Headers)
            .Select(header => header.Key + "=" + string.Join(",", header.Value)));

    private static ServiceSettingDefinition[] Catalog() =>
    [
        new("billing", ServiceSettingValueType.String),
        new("billing.rate", ServiceSettingValueType.Number),
        new("product.string", ServiceSettingValueType.String),
        new("product.number", ServiceSettingValueType.Number),
        new("product.boolean", ServiceSettingValueType.Boolean),
        new("product.json", ServiceSettingValueType.Json),
        new("product.default", ServiceSettingValueType.Number, defaultValue: "3.00", requiresRestart: true),
        new("product.missing", ServiceSettingValueType.String),
        new("product.secret-string", ServiceSettingValueType.String, isSensitive: true),
        new("product.secret-number", ServiceSettingValueType.Number, isSensitive: true),
        new("product.secret-boolean", ServiceSettingValueType.Boolean, isSensitive: true),
        new("product.secret-json", ServiceSettingValueType.Json, isRequired: true, isSensitive: true),
    ];

    private static ServiceSettingSnapshotRead Snapshot() => SettingQueryHostFixture.Read(
        7,
        SettingQueryHostFixture.Value("billing", 7, ServiceSettingValueType.String, "acme"),
        SettingQueryHostFixture.Value("billing.rate", 7, ServiceSettingValueType.Number, "1.250"),
        SettingQueryHostFixture.Value("product.string", 7, ServiceSettingValueType.String, "Orders"),
        SettingQueryHostFixture.Value("product.number", 7, ServiceSettingValueType.Number, "2.500"),
        SettingQueryHostFixture.Value("product.boolean", 7, ServiceSettingValueType.Boolean, "TRUE"),
        SettingQueryHostFixture.Value("product.json", 7, ServiceSettingValueType.Json, "{ \"mode\" : true }"),
        Sensitive("product.secret-string", 7, ServiceSettingValueType.String, "secret-string-plaintext"),
        Sensitive("product.secret-number", 7, ServiceSettingValueType.Number, "42.5"),
        Sensitive("product.secret-boolean", 7, ServiceSettingValueType.Boolean, "true"),
        Sensitive("product.secret-json", 7, ServiceSettingValueType.Json, "{\"token\":\"secret\"}"));

    private static PersistedServiceSettingValue Sensitive(
        string key, long version, ServiceSettingValueType valueType, string plaintext) =>
        SettingQueryHostFixture.Value(
            key, version, valueType, SettingQueryHostFixture.Protect(key, plaintext, RootKey));

    private static ServiceSettingDefinition[] FailureCatalog() =>
    [
        new("product.name", ServiceSettingValueType.String, isRequired: true),
        new("product.rate", ServiceSettingValueType.Number,
            constraints: [new NumberRangeSettingConstraint(0, 10)]),
        new("product.secret", ServiceSettingValueType.String, isSensitive: true),
    ];

    private static FailurePlan Plan(string scenario)
    {
        var healthy = SettingQueryHostFixture.Read(
            5,
            SettingQueryHostFixture.Value("product.name", 5, ServiceSettingValueType.String, "orders"));
        return scenario switch
        {
            "unknown-key" => new(healthy, SettingQueryHostFixture.Read(
                6, SettingQueryHostFixture.Value("unknown.key", 6, ServiceSettingValueType.String, "x"))),
            "mixed-version" => new(healthy, SettingQueryHostFixture.Read(
                6, SettingQueryHostFixture.Value("product.name", 5, ServiceSettingValueType.String, "x"))),
            "type-mismatch" => new(healthy, SettingQueryHostFixture.Read(
                6, SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.Number, "1"))),
            "missing-required" => new(healthy, SettingQueryHostFixture.Read(6)),
            "envelope-required" => new(healthy, SettingQueryHostFixture.Read(
                6,
                SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.String, "orders"),
                SettingQueryHostFixture.Value("product.secret", 6, ServiceSettingValueType.String, "plain"))),
            "corrupt-ciphertext" => new(healthy, SettingQueryHostFixture.Read(
                6,
                SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.String, "orders"),
                SettingQueryHostFixture.Value("product.secret", 6, ServiceSettingValueType.String, "sm:v1:not-base64"))),
            "wrong-root-key" => new(healthy, SettingQueryHostFixture.Read(
                6,
                SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.String, "orders"),
                SettingQueryHostFixture.Value("product.secret", 6, ServiceSettingValueType.String,
                    SettingQueryHostFixture.Protect("product.secret", "x", "another-root-key-with-entropy")))),
            "missing-root-key" => Unprimed(SettingQueryHostFixture.Read(
                6,
                SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.String, "orders"),
                SettingQueryHostFixture.Value("product.secret", 6, ServiceSettingValueType.String,
                    SettingQueryHostFixture.Protect("product.secret", "x", RootKey)))),
            "constraint" => new(healthy, SettingQueryHostFixture.Read(
                6,
                SettingQueryHostFixture.Value("product.name", 6, ServiceSettingValueType.String, "orders"),
                SettingQueryHostFixture.Value("product.rate", 6, ServiceSettingValueType.Number, "99"))),
            "composite" => new(healthy, healthy, Composite: true, Prime: false),
            "storage-exception" => new(healthy, healthy,
                Failure: new InvalidOperationException(SettingQueryHostFixture.Secret)),
            "null-read" => new(healthy, null),
            "internal-cancel" => new(healthy, healthy,
                Failure: new OperationCanceledException(SettingQueryHostFixture.Secret)),
            "stale-version" => new(healthy, SettingQueryHostFixture.Read(
                3, SettingQueryHostFixture.Value("product.name", 3, ServiceSettingValueType.String, "orders"))),
            "same-version-conflict" => new(healthy, SettingQueryHostFixture.Read(
                5, SettingQueryHostFixture.Value("product.name", 5, ServiceSettingValueType.String, "changed"))),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    /// <summary>Describes a first read that already fails, so there is no successful version to prime.</summary>
    private static FailurePlan Unprimed(ServiceSettingSnapshotRead read) =>
        new(read, read, RootKey: false, Prime: false);

    private sealed record FailurePlan(
        ServiceSettingSnapshotRead Initial,
        ServiceSettingSnapshotRead? Mutated,
        Exception? Failure = null,
        bool RootKey = true,
        bool Composite = false,
        bool Prime = true);

    private sealed class RootKeySource(string rootKey) : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rootKey);
    }

    private sealed class RejectingValidator : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context) =>
            [new ServiceSettingValidationError("product.name", "setting.composite_rejected")];
    }

    private sealed class SecretCarryingConstraint(string secret) : IServiceSettingValueConstraint
    {
        public ServiceSettingValueType ValueType => ServiceSettingValueType.String;

        public string ErrorCode => "setting.secret_constraint";

        public bool IsSatisfied(ServiceSettingValue value) => true;

        public override string ToString() => secret;
    }

    /// <summary>A cooperative source whose first read blocks until the test releases it.</summary>
    private sealed class BlockingSource : IServiceSettingSnapshotSource
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int calls;

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls => Volatile.Read(ref calls);

        internal CancellationToken ObservedToken { get; private set; }

        public async ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                ObservedToken = cancellationToken;
                Entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return SettingQueryHostFixture.Read(
                call,
                SettingQueryHostFixture.Value(
                    "product.string", call, ServiceSettingValueType.String, "version-" + call));
        }

        internal void Release() => release.TrySetResult();

        /// <summary>Waits until the second request can only be blocked on the refresh lock.</summary>
        internal async Task WaitForPendingAsync()
        {
            // The first read still holds the loader's lock, so a second call cannot be counted.
            await Task.Delay(50, Token);
            Assert.Equal(1, Calls);
        }
    }
}

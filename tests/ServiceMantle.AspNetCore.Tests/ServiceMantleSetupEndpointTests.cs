using System.Net;
using Microsoft.AspNetCore.Builder;
using ServiceMantle.Health;
using ServiceMantle.Http;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

using Fixture = SetupHostFixture;

public sealed class ServiceMantleSetupEndpointTests
{
    private const string CredentialInvalid =
        "{\"errorCode\":\"management.setup.credential_invalid\"}";

    private const string Unavailable = "{\"errorCode\":\"management.setup.unavailable\"}";

    [Fact]
    public async Task The_entries_are_opt_in_and_map_once_with_an_explicit_executor()
    {
        await using var unmapped = await Fixture.CreateAsync(mapCount: 0);
        await unmapped.StartAsync();
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(mapCount: 2));
        var withoutExecutor = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(mapExecutor: false));

        using var read = await unmapped.ReadAsync();
        using var complete = await unmapped.CompleteAsync();

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        Assert.NotEqual(duplicate.Message, withoutExecutor.Message);
    }

    [Theory]
    [InlineData(InstallationStatus.PendingSetup, "{\"status\":\"pending\"}")]
    [InlineData(InstallationStatus.Completed, "{\"status\":\"completed\"}")]
    public async Task The_read_entry_projects_only_the_fixed_status(
        InstallationStatus installed,
        string expected)
    {
        await using var fixture = await Fixture.StartAsync(
            installed: installed,
            phase: installed == InstallationStatus.Completed
                ? ServiceStartupPhase.Completed
                : ServiceStartupPhase.PendingSetup);

        using var response = await fixture.ReadAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, body);
        Assert.Equal(1, fixture.Store.Reads);
    }

    [Fact]
    public async Task Head_answers_the_same_status_and_headers_without_a_body()
    {
        await using var fixture = await Fixture.StartAsync();

        using var get = await fixture.ReadAsync();
        using var head = await fixture.ReadAsync(HttpMethod.Head);
        var getBody = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headBody = await head.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(
            get.Content.Headers.ContentType?.ToString(),
            head.Content.Headers.ContentType?.ToString());
        Assert.Equal(getBody.Length, get.Content.Headers.ContentLength);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(string.Empty, headBody);
    }

    [Fact]
    public async Task A_missing_absent_or_failing_installation_authority_is_unavailable()
    {
        const string secret = "Server=db.internal;Password=installation-secret";
        await using var missing = await Fixture.StartAsync(registerStore: false);
        await using var absent = await Fixture.StartAsync(installed: null);
        await using var failing = await Fixture.StartAsync();
        failing.Store.Failure = new InvalidOperationException(secret);

        using var withoutStore = await missing.ReadAsync();
        using var withoutRow = await absent.ReadAsync();
        using var withFailure = await failing.ReadAsync();
        using var completeWithFailure = await failing.CompleteAsync();

        await AssertBodyAsync(withoutStore, HttpStatusCode.ServiceUnavailable, Unavailable);
        await AssertBodyAsync(withoutRow, HttpStatusCode.ServiceUnavailable, Unavailable);
        var body = await AssertBodyAsync(withFailure, HttpStatusCode.ServiceUnavailable, Unavailable);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        await AssertBodyAsync(completeWithFailure, HttpStatusCode.ServiceUnavailable, Unavailable);
        Assert.Equal(0, failing.Executor.Calls);
    }

    [Fact]
    public async Task A_completed_installation_is_a_stable_conflict_that_never_parses_the_code()
    {
        await using var fixture = await Fixture.StartAsync(
            installed: InstallationStatus.Completed,
            phase: ServiceStartupPhase.Completed);

        // Neither a conforming nor an unusable body changes the answer, and neither is parsed.
        using var conforming = await fixture.CompleteAsync();
        using var garbage = await fixture.CompleteAsync(body: "not json at all", contentType: "text/plain");
        using var replay = await fixture.CompleteAsync();

        foreach (var response in new[] { conforming, garbage, replay })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(
                ServiceMantleManagementApiDefaults.ConflictErrorCode,
                body,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.SentinelCode, body, StringComparison.Ordinal);
        }

        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Theory]
    // Media type, query string and content encoding.
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", "text/plain", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", "application/json; charset=utf-16", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", null, null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", "application/json", "?force=1", null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", "application/json", null, "gzip")]
    // Document shape.
    [InlineData("[]", "application/json", null, null)]
    [InlineData("\"" + SetupHostFixture.SentinelCode + "\"", "application/json", null, null)]
    [InlineData("{}", "application/json", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\",\"extra\":1}", "application/json", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\",\"code\":\"" + SetupHostFixture.SentinelCode + "\"}", "application/json", null, null)]
    [InlineData("{\"Code\":\"" + SetupHostFixture.SentinelCode + "\"}", "application/json", null, null)]
    [InlineData("{\"code\":null}", "application/json", null, null)]
    [InlineData("{\"code\":123}", "application/json", null, null)]
    [InlineData("{\"code\":{\"value\":\"x\"}}", "application/json", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\",}", "application/json", null, null)]
    [InlineData("{\"code\":\"" + SetupHostFixture.SentinelCode + "\"} trailing", "application/json", null, null)]
    // Code value: 31 and 33 characters, an illegal character, and an untrimmed value.
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-AB\"}", "application/json", null, null)]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-ABCD\"}", "application/json", null, null)]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-A+B\"}", "application/json", null, null)]
    [InlineData("{\"code\":\" " + SetupHostFixture.SentinelCode + " \"}", "application/json", null, null)]
    public async Task Every_unusable_request_shape_is_the_fixed_invalid_request(
        string body,
        string? contentType,
        string? query,
        string? contentEncoding)
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.CompleteAsync(
            body: body,
            contentType: contentType,
            path: query is null ? null : fixture.SetupPath + query,
            contentEncoding: contentEncoding);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ServiceMantleManagementApiDefaults.InvalidRequestErrorCode,
            raw,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SentinelCode, raw, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task The_raw_body_and_json_depth_limits_are_enforced()
    {
        await using var fixture = await Fixture.StartAsync();
        var padding = new string('p', ServiceMantleSetupRequestParser.MaximumBodyLength);
        var oversized = "{\"code\":\"" + SetupHostFixture.SentinelCode + "\",\"pad\":\"" + padding + "\"}";
        // Five nested objects exceed the depth of four before the value is even considered.
        const string tooDeep = "{\"code\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}";

        using var big = await fixture.CompleteAsync(body: oversized);
        using var deep = await fixture.CompleteAsync(body: tooDeep);

        Assert.True(oversized.Length > ServiceMantleSetupRequestParser.MaximumBodyLength);
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task A_conforming_request_reaches_the_executor_with_the_exact_untrimmed_code()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.CompleteAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, body);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(Fixture.SentinelCode, Assert.Single(fixture.Executor.Codes));
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Theory]
    [InlineData(ServiceMantleSetupCompletionStatus.CredentialInvalid, HttpStatusCode.Unauthorized)]
    [InlineData(ServiceMantleSetupCompletionStatus.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ServiceMantleSetupCompletionStatus.ValidationFailed, HttpStatusCode.BadRequest)]
    [InlineData(ServiceMantleSetupCompletionStatus.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Every_completion_status_maps_to_one_fixed_response(
        ServiceMantleSetupCompletionStatus status,
        HttpStatusCode expected)
    {
        await using var fixture = await Fixture.StartAsync(completion: Result(status));

        using var response = await fixture.CompleteAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(Fixture.SentinelCode, body, StringComparison.Ordinal);
        if (status == ServiceMantleSetupCompletionStatus.CredentialInvalid)
        {
            // One response for invalid, malformed, expired, mismatched, never-issued and replayed.
            Assert.Equal(CredentialInvalid, body);
        }
    }

    [Fact]
    public async Task A_null_result_an_undefined_status_and_an_executor_exception_are_unavailable()
    {
        const string secret = "contributor-secret-value";
        await using var nullResult = await Fixture.StartAsync();
        nullResult.Executor.Result = null;
        await using var undefined = await Fixture.StartAsync();
        undefined.Executor.Result = Result((ServiceMantleSetupCompletionStatus)999);
        await using var throwing = await Fixture.StartAsync();
        throwing.Executor.Failure = new InvalidOperationException(secret);

        using var withNull = await nullResult.CompleteAsync();
        using var withUndefined = await undefined.CompleteAsync();
        using var withException = await throwing.CompleteAsync();

        await AssertBodyAsync(withNull, HttpStatusCode.ServiceUnavailable, Unavailable);
        await AssertBodyAsync(withUndefined, HttpStatusCode.ServiceUnavailable, Unavailable);
        var body = await AssertBodyAsync(withException, HttpStatusCode.ServiceUnavailable, Unavailable);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_cancellation_inside_the_executor_is_unavailable()
    {
        await using var fixture = await Fixture.StartAsync();
        // A cancellation the caller did not request must not look like caller cancellation.
        fixture.Executor.Failure = new OperationCanceledException(
            "internal setup cancellation secret",
            new CancellationTokenSource(0).Token);

        using var response = await fixture.CompleteAsync();

        var body = await AssertBodyAsync(response, HttpStatusCode.ServiceUnavailable, Unavailable);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_fixed_result()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Executor.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = fixture.CreateAbort();

        var request = fixture.CompleteAsync(abort: abort);
        await fixture.Executor.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Fact]
    public async Task Two_concurrent_callers_reach_the_executor_and_only_one_completion_wins()
    {
        // The consumer transaction is the concurrency authority: the endpoint forwards both attempts
        // untouched and answers exactly what each transaction concluded.
        await using var fixture = await Fixture.StartAsync();
        fixture.Executor.ExpectedCalls = 2;
        fixture.Executor.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Selector = call => call == 1
            ? ServiceMantleSetupCompletionResult.Committed()
            : ServiceMantleSetupCompletionResult.Conflict();

        var first = fixture.CompleteAsync();
        var second = fixture.CompleteAsync();
        await fixture.Executor.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Executor.Gate.SetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;

        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };
        Assert.Equal(2, fixture.Executor.Calls);
        Assert.Single(statuses, status => status == HttpStatusCode.NoContent);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        Assert.Equal(
            [SetupHostFixture.SentinelCode, SetupHostFixture.SentinelCode],
            fixture.Executor.Codes);

        // The completed installation then makes every replay a stable conflict.
        fixture.Store.Status = InstallationStatus.Completed;
        using var replay = await fixture.CompleteAsync();
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(2, fixture.Executor.Calls);
    }

    [Theory]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Unreachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Failed, ServiceDatabaseReadinessState.Reachable)]
    public async Task The_shared_gate_rejects_a_wrong_phase_before_any_handler(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migrationStatus,
        ServiceDatabaseReadinessState databaseStatus)
    {
        await using var fixture = await Fixture.StartAsync(
            phase: phase,
            migrationStatus: migrationStatus,
            databaseStatus: databaseStatus);

        using var read = await fixture.ReadAsync();
        using var complete = await fixture.CompleteAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, complete.StatusCode);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task The_unsafe_request_header_is_required_exactly_once()
    {
        await using var fixture = await Fixture.StartAsync();

        using var missing = await fixture.CompleteAsync(unsafeHeader: []);
        using var wrongValue = await fixture.CompleteAsync(unsafeHeader: ["0"]);
        using var repeated = await fixture.CompleteAsync(unsafeHeader: ["1", "1"]);

        foreach (var response in new[] { missing, wrongValue, repeated })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The guard runs before the handler, so nothing was read and nothing was executed.
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task Both_entries_share_the_setup_quota_and_a_rejection_executes_nothing()
    {
        await using var fixture = await Fixture.StartAsync(setupPermitLimit: 2);

        using var first = await fixture.ReadAsync();
        using var second = await fixture.CompleteAsync();
        using var third = await fixture.ReadAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(2, fixture.Store.Reads);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Fact]
    public async Task A_custom_versioned_root_moves_both_entries()
    {
        await using var fixture = await Fixture.StartAsync(root: "/ops/admin/v1");

        using var moved = await fixture.ReadAsync(path: "/ops/admin/v1/setup");
        using var defaultRoot = await fixture.ReadAsync(path: "/management/v1/setup");

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, defaultRoot.StatusCode);
    }

    [Fact]
    public async Task Security_headers_and_the_correlation_contract_apply_to_every_outcome()
    {
        await using var fixture = await Fixture.StartAsync();

        using var read = await fixture.ReadAsync();
        using var invalid = await fixture.CompleteAsync(body: "{}");
        using var success = await fixture.CompleteAsync();

        foreach (var response in new[] { read, invalid, success })
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.Single(response.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId))));
        }
    }

    private static ServiceMantleSetupCompletionResult Result(ServiceMantleSetupCompletionStatus status) =>
        status switch
        {
            ServiceMantleSetupCompletionStatus.Committed => ServiceMantleSetupCompletionResult.Committed(),
            ServiceMantleSetupCompletionStatus.CredentialInvalid =>
                ServiceMantleSetupCompletionResult.CredentialInvalid(),
            ServiceMantleSetupCompletionStatus.Conflict => ServiceMantleSetupCompletionResult.Conflict(),
            ServiceMantleSetupCompletionStatus.ValidationFailed =>
                ServiceMantleSetupCompletionResult.ValidationFailed(),
            _ => ServiceMantleSetupCompletionResult.Unavailable(),
        };

    private static async Task<string> AssertBodyAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string body)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, raw);
        return raw;
    }
}

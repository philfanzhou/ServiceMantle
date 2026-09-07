using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ServiceMantle.Health;
using ServiceMantle.Http;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

using Fixture = ManagementSessionHostFixture;

public sealed class ServiceMantleManagementSessionEndpointTests
{
    private const string Unavailable = "{\"errorCode\":\"management.session.unavailable\"}";

    private const string Unauthenticated = "{\"errorCode\":\"management.session.unauthenticated\"}";

    private static string CookieName => ServiceMantleManagementSessionDefaults.CookieName;

    [Fact]
    public async Task The_entries_are_opt_in_and_map_once_with_a_valid_adapter_and_budget()
    {
        await using var unmapped = await Fixture.CreateAsync(mapCount: 0);
        await unmapped.StartAsync();
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(mapCount: 2));
        var withoutAdapter = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(mapAdapter: false));
        var tooShort = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(loginTimeout: TimeSpan.FromMilliseconds(99)));
        var tooLong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(loginTimeout: TimeSpan.FromSeconds(31)));

        using var login = await unmapped.LoginAsync();
        using var session = await unmapped.ReadSessionAsync();
        using var logout = await unmapped.LogoutAsync();

        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, logout.StatusCode);
        Assert.NotEqual(duplicate.Message, withoutAdapter.Message);
        Assert.Equal(tooShort.Message, tooLong.Message);
    }

    [Fact]
    public async Task The_boundary_login_timeouts_are_accepted()
    {
        await using var shortest = await Fixture.StartAsync(loginTimeout: TimeSpan.FromMilliseconds(100));
        await using var longest = await Fixture.StartAsync(loginTimeout: TimeSpan.FromSeconds(30));

        using var first = await shortest.LoginAsync();
        using var second = await longest.LoginAsync();

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_login_issues_exactly_one_fixed_cookie_after_sign_in()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.LoginAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, body);
        var issued = Assert.Single(cookies);
        Assert.StartsWith(CookieName + "=", issued, StringComparison.Ordinal);
        Assert.Contains("httponly", issued, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Adapter.Calls);
        // The adapter received the raw credential body; the response carries none of it.
        Assert.Contains(Fixture.SentinelPassword, Assert.Single(fixture.Adapter.Bodies), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SentinelPassword, issued, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_login_keeps_the_existing_session_401_and_issues_no_cookie()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Adapter.Result = ManagementIdentityResult.Unauthenticated();

        using var response = await fixture.LoginAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Unauthenticated, body);
        Assert.Equal(
            "application/json; charset=utf-8",
            response.Content.Headers.ContentType?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Every_unusable_provider_outcome_is_the_fixed_unavailable_and_issues_no_cookie()
    {
        await using var failed = await Fixture.StartAsync();
        failed.Adapter.Result = ManagementIdentityResult.Failed(Fixture.SentinelProviderCode);
        await using var nullResult = await Fixture.StartAsync();
        nullResult.Adapter.Result = null;
        await using var throwing = await Fixture.StartAsync();
        throwing.Adapter.Failure = new InvalidOperationException(Fixture.SentinelException);
        await using var timedOut = await Fixture.StartAsync(loginTimeout: TimeSpan.FromMilliseconds(100));
        timedOut.Adapter.Hang = true;

        using var withFailure = await failed.LoginAsync();
        using var withNull = await nullResult.LoginAsync();
        using var withException = await throwing.LoginAsync();
        using var withTimeout = await timedOut.LoginAsync();

        foreach (var response in new[] { withFailure, withNull, withException, withTimeout })
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(Unavailable, body);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            // A consumer-supplied provider code is never forwarded, and neither is an exception.
            Assert.DoesNotContain(Fixture.SentinelProviderCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.SentinelException, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.SentinelPassword, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_login_admission_envelope_is_enforced_before_the_adapter_runs()
    {
        await using var fixture = await Fixture.StartAsync();
        var oversized = "{\"password\":\"" +
            new string('p', (int)ServiceMantleManagementSessionMapping.MaximumLoginBodyLength) + "\"}";

        using var withQuery = await fixture.LoginAsync(path: fixture.LoginPath + "?force=1");
        using var withEncoding = await fixture.LoginAsync(contentEncoding: "gzip");
        using var tooLarge = await fixture.LoginAsync(body: oversized);
        using var withoutHeader = await fixture.LoginAsync(unsafeHeader: []);
        using var repeatedHeader = await fixture.LoginAsync(unsafeHeader: ["1", "1"]);

        foreach (var response in new[]
        {
            withQuery, withEncoding, tooLarge, withoutHeader, repeatedHeader,
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(
                ServiceMantleManagementApiDefaults.InvalidRequestErrorCode,
                body,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.SentinelPassword, body, StringComparison.Ordinal);
        }

        Assert.Equal(0, fixture.Adapter.Calls);
        Assert.True(oversized.Length > ServiceMantleManagementSessionMapping.MaximumLoginBodyLength);
    }

    [Fact]
    public async Task A_body_at_the_admission_limit_still_reaches_the_adapter()
    {
        await using var fixture = await Fixture.StartAsync();
        var prefix = "{\"password\":\"";
        var suffix = "\"}";
        var padding = new string(
            'p',
            (int)ServiceMantleManagementSessionMapping.MaximumLoginBodyLength - prefix.Length - suffix.Length);

        using var response = await fixture.LoginAsync(body: prefix + padding + suffix);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionMapping.MaximumLoginBodyLength,
            Encoding.UTF8.GetByteCount(Assert.Single(fixture.Adapter.Bodies)));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_fixed_result()
    {
        await using var fixture = await Fixture.StartAsync(loginTimeout: TimeSpan.FromSeconds(30));
        fixture.Adapter.Hang = true;
        var abort = fixture.CreateAbort();

        var request = fixture.LoginAsync(abort: abort);
        await fixture.Adapter.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task Concurrent_logins_each_get_their_own_body_and_token()
    {
        await using var fixture = await Fixture.StartAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            fixture.LoginAsync(body: "{\"password\":\"" + Fixture.SentinelPassword + index + "\"}")));
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            response.Dispose();
        }

        Assert.Equal(8, fixture.Adapter.Calls);
        Assert.Equal(8, fixture.Adapter.Bodies.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, fixture.Adapter.Tokens.Distinct().Count());
    }

    [Fact]
    public async Task The_current_session_projects_exactly_three_fields_in_the_fixed_order()
    {
        await using var fixture = await Fixture.StartAsync();
        var cookie = fixture.Cookie([ManagementPermission.Admin, ManagementPermission.Read]);

        using var response = await fixture.ReadSessionAsync(cookie);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["authenticated", "expiresAtUtc", "permissions"],
            body.EnumerateObject().Select(property => property.Name));
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.EndsWith("Z", body.GetProperty("expiresAtUtc").GetString(), StringComparison.Ordinal);
        Assert.True(body.GetProperty("expiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        // Permissions keep the fixed ManagementPermission order regardless of the claim order.
        Assert.Equal(
            [ManagementPermissions.ReadValue, ManagementPermissions.AdminValue],
            body.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));
        // No operator identity, display name, source, or ticket material is projected.
        foreach (var secret in new[] { "operator-1", "sensitive-display-name", "interactive" })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Head_answers_the_same_status_and_headers_without_a_body()
    {
        await using var fixture = await Fixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Read);

        using var get = await fixture.ReadSessionAsync(cookie);
        using var head = await fixture.ReadSessionAsync(cookie, HttpMethod.Head);
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
    public async Task A_non_admin_identity_may_read_the_session_and_log_out()
    {
        await using var fixture = await Fixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Read);

        using var read = await fixture.ReadSessionAsync(cookie);
        using var logout = await fixture.LogoutAsync(cookie);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task Logout_expires_this_client_cookie_and_revokes_no_copy()
    {
        await using var fixture = await Fixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Read);

        using var logout = await fixture.LogoutAsync(cookie);
        var deletion = Assert.Single(logout.Headers.GetValues("Set-Cookie"));
        // The very same ticket, presented again from another client, still authenticates: logout is
        // local and stateless and adds no server-side revocation authority.
        using var copied = await fixture.ReadSessionAsync(cookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(
            string.Empty,
            await logout.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith(CookieName + "=;", deletion, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
    }

    [Fact]
    public async Task Missing_expired_and_invalid_claim_cookies_keep_the_existing_contract()
    {
        await using var fixture = await Fixture.StartAsync();
        var expired = fixture.Cookie(ManagementPermission.Read, age: TimeSpan.FromHours(2));
        var corrupt = CookieName + "=not-a-protected-ticket";
        var invalidClaims = fixture.CookieWithInvalidClaims();

        using var withoutCookie = await fixture.ReadSessionAsync();
        using var withExpired = await fixture.ReadSessionAsync(expired);
        using var withCorrupt = await fixture.ReadSessionAsync(corrupt);
        using var withInvalidClaims = await fixture.ReadSessionAsync(invalidClaims);
        using var logoutWithoutCookie = await fixture.LogoutAsync();
        using var logoutWithInvalidClaims = await fixture.LogoutAsync(invalidClaims);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutCookie.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode,
            await ErrorCodeAsync(withoutCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, withExpired.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ExpiredErrorCode,
            await ErrorCodeAsync(withExpired));
        Assert.Equal(HttpStatusCode.Unauthorized, withCorrupt.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withInvalidClaims.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ForbiddenErrorCode,
            await ErrorCodeAsync(withInvalidClaims));
        Assert.Equal(HttpStatusCode.Unauthorized, logoutWithoutCookie.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, logoutWithInvalidClaims.StatusCode);
        // None of these rejections executed a sign-out, so none of them wrote a cookie.
        foreach (var response in new[]
        {
            withoutCookie, withExpired, withCorrupt, withInvalidClaims,
            logoutWithoutCookie, logoutWithInvalidClaims,
        })
        {
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Theory]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Unreachable)]
    public async Task The_shared_gate_rejects_a_not_ready_service_before_any_handler(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migrationStatus,
        ServiceDatabaseReadinessState databaseStatus)
    {
        await using var fixture = await Fixture.StartAsync(
            phase: phase,
            migrationStatus: migrationStatus,
            databaseStatus: databaseStatus);
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        using var login = await fixture.LoginAsync();
        using var read = await fixture.ReadSessionAsync(cookie);
        using var logout = await fixture.LogoutAsync(cookie);

        foreach (var response in new[] { login, read, logout })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task Login_uses_the_setup_quota_and_the_session_entries_use_the_operator_quota()
    {
        await using var fixture = await Fixture.StartAsync(
            setupPermitLimit: 2,
            managementPermitLimit: 2);
        var cookie = fixture.Cookie(ManagementPermission.Read, "operator-1");
        var other = fixture.Cookie(ManagementPermission.Read, "operator-2");

        using var firstLogin = await fixture.LoginAsync();
        using var secondLogin = await fixture.LoginAsync();
        using var thirdLogin = await fixture.LoginAsync();
        using var firstRead = await fixture.ReadSessionAsync(cookie);
        using var secondRead = await fixture.ReadSessionAsync(cookie);
        using var thirdRead = await fixture.ReadSessionAsync(cookie);
        using var otherOperator = await fixture.ReadSessionAsync(other);

        Assert.Equal(HttpStatusCode.NoContent, firstLogin.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondLogin.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, thirdLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, thirdRead.StatusCode);
        // A different operator keeps its own quota, so the session entries are operator-partitioned.
        Assert.Equal(HttpStatusCode.OK, otherOperator.StatusCode);
        Assert.Equal(2, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task Two_instances_sharing_a_key_ring_accept_each_other_cookie_and_a_foreign_one_fails()
    {
        var directory = Directory.CreateTempSubdirectory("servicemantle-session-keys");
        try
        {
            await using var first = await Fixture.StartAsync(keyRingDirectory: directory.FullName);
            await using var second = await Fixture.StartAsync(keyRingDirectory: directory.FullName);
            await using var isolated = await Fixture.StartAsync();
            var shared = first.Cookie(ManagementPermission.Read);
            var foreign = isolated.Cookie(ManagementPermission.Read);

            using var onFirst = await first.ReadSessionAsync(shared);
            using var onSecond = await second.ReadSessionAsync(shared);
            using var withForeign = await second.ReadSessionAsync(foreign);

            Assert.Equal(HttpStatusCode.OK, onFirst.StatusCode);
            Assert.Equal(HttpStatusCode.OK, onSecond.StatusCode);
            // A different key ring fails closed rather than degrading to an anonymous read.
            Assert.Equal(HttpStatusCode.Unauthorized, withForeign.StatusCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_protected_v1_group_stays_admin_only_beside_the_anonymous_login()
    {
        await using var fixture = await Fixture.StartAsync(mapProtectedGroup: true);
        var read = fixture.Cookie(ManagementPermission.Read);
        var admin = fixture.Cookie(ManagementPermission.Admin);

        using var anonymous = await fixture.GetAsync(fixture.Root + "/runtime");
        using var nonAdmin = await fixture.GetAsync(fixture.Root + "/runtime", read);
        using var withAdmin = await fixture.GetAsync(fixture.Root + "/runtime", admin);
        using var login = await fixture.LoginAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withAdmin.StatusCode);
        // The anonymous exception exists only in the shared entry baseline.
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task A_custom_versioned_root_moves_all_three_entries()
    {
        await using var fixture = await Fixture.StartAsync(root: "/ops/admin/v1");
        var cookie = fixture.Cookie(ManagementPermission.Read);

        using var moved = await fixture.ReadSessionAsync(cookie, path: "/ops/admin/v1/session");
        using var movedLogin = await fixture.LoginAsync(path: "/ops/admin/v1/session/login");
        using var defaultRoot = await fixture.ReadSessionAsync(cookie, path: "/management/v1/session");

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, movedLogin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, defaultRoot.StatusCode);
    }

    [Fact]
    public async Task Security_headers_and_the_correlation_contract_apply_to_every_outcome()
    {
        await using var fixture = await Fixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Read);

        using var login = await fixture.LoginAsync();
        using var read = await fixture.ReadSessionAsync(cookie);
        using var logout = await fixture.LogoutAsync(cookie);
        using var rejected = await fixture.LoginAsync(unsafeHeader: []);

        foreach (var response in new[] { login, read, logout, rejected })
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.Single(response.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId))));
        }
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return raw.Length == 0
            ? null
            : JsonDocument.Parse(raw).RootElement.GetProperty("errorCode").GetString();
    }
}

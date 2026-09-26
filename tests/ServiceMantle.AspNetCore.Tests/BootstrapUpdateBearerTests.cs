using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi.Bootstrap;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.Management;
using Xunit;
using BearerHandler = ServiceMantle.AspNetCore.Tests.BootstrapManagementHostFixture.TestBearerHandler;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the opt-in management Bearer credential of the Bootstrap update entry: the default stays
/// cookie-only, an enabled host selects exactly one scheme per request and never merges or falls
/// back, no other entry gains the scheme, and every unusable configuration fails at startup with a
/// fixed message.
/// </summary>
public sealed class BootstrapUpdateBearerTests
{
    private const string InvalidRequest = "management.request.invalid";
    private const string Restart = """{"restartRequired":true}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Default: identical to the cookie-only entry ----

    [Fact]
    public async Task Without_the_option_a_bearer_credential_is_ignored()
    {
        await using var fixture = await StartAsync(enabled: false);

        using var bearerOnly = await fixture.SendAsync(
            HttpMethod.Put,
            authorization: ["Bearer " + BearerHandler.AdminToken]);
        using var bearerAndCookie = await fixture.SendAsync(
            HttpMethod.Put,
            cookie: fixture.Cookie(ManagementPermission.Admin, "cookie-admin"),
            authorization: ["Bearer " + BearerHandler.ReaderToken]);

        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnly.StatusCode);
        Assert.Equal(ManagementSessionDefaults.UnauthenticatedErrorCode, await ReadErrorCodeAsync(bearerOnly));
        Assert.Equal(HttpStatusCode.OK, bearerAndCookie.StatusCode);
        Assert.Equal(["cookie-admin"], fixture.AuthorizedOperators);
        Assert.Equal(1, UpdateCalls(fixture));
    }

    // ---- Enabled: one scheme per request ----

    [Fact]
    public async Task An_administrator_bearer_updates_and_is_the_authorized_operator()
    {
        await using var fixture = await StartAsync(enabled: true);

        using var response = await fixture.SendAsync(
            HttpMethod.Put,
            authorization: ["Bearer " + BearerHandler.AdminToken]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Restart, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["bearer-admin"], fixture.AuthorizedOperators);
        Assert.Equal(1, UpdateCalls(fixture));
    }

    [Fact]
    public async Task A_cookie_without_authorization_keeps_working()
    {
        await using var fixture = await StartAsync(enabled: true);

        using var admin = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Admin, "cookie-admin"));
        using var reader = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Read, "cookie-reader"));
        using var none = await fixture.PutAsync(cookie: null);

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Equal(ManagementSessionDefaults.ForbiddenErrorCode, await ReadErrorCodeAsync(reader));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(ManagementSessionDefaults.UnauthenticatedErrorCode, await ReadErrorCodeAsync(none));
        Assert.Equal(["cookie-admin"], fixture.AuthorizedOperators);
    }

    public static TheoryData<string[]> UnusableAuthorization => new()
    {
        new[] { "Bearer not-a-token" },
        new[] { "Basic Y29va2llOmFkbWlu" },
        new[] { "Bearer" },
        new[] { "Bearer " + BearerHandler.AdminToken, "Bearer " + BearerHandler.AdminToken },
    };

    [Theory]
    [MemberData(nameof(UnusableAuthorization))]
    public async Task An_unusable_bearer_is_rejected_even_beside_a_valid_cookie(string[] authorization)
    {
        await using var fixture = await StartAsync(enabled: true);

        using var response = await fixture.SendAsync(
            HttpMethod.Put,
            cookie: fixture.Cookie(ManagementPermission.Admin, "cookie-admin"),
            authorization: authorization);

        // The Bearer scheme answered with its own failure; the cookie was never consulted.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(BearerHandler.UnauthenticatedCode, await ReadErrorCodeAsync(response));
        Assert.Empty(fixture.AuthorizedOperators);
        Assert.Equal(0, UpdateCalls(fixture));
    }

    [Fact]
    public async Task A_bearer_without_the_administrator_permission_is_forbidden_by_its_scheme()
    {
        await using var fixture = await StartAsync(enabled: true);

        using var response = await fixture.SendAsync(
            HttpMethod.Put,
            cookie: fixture.Cookie(ManagementPermission.Admin, "cookie-admin"),
            authorization: ["Bearer " + BearerHandler.ReaderToken]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(BearerHandler.ForbiddenCode, await ReadErrorCodeAsync(response));
        Assert.Empty(fixture.AuthorizedOperators);
        Assert.Equal(0, UpdateCalls(fixture));
    }

    [Fact]
    public async Task The_unsafe_request_header_and_the_phase_still_come_first()
    {
        await using var fixture = await StartAsync(enabled: true);
        var bearer = new[] { "Bearer " + BearerHandler.AdminToken };

        using var noHeader = await fixture.SendAsync(HttpMethod.Put, unsafeHeader: [], authorization: bearer);
        fixture.Snapshot.Current = BootstrapManagementHostFixture.BeforeConfiguration;
        using var wrongPhase = await fixture.SendAsync(HttpMethod.Put, authorization: bearer);

        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(InvalidRequest, await ReadErrorCodeAsync(noHeader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, wrongPhase.StatusCode);
        Assert.Equal(0, UpdateCalls(fixture));
    }

    [Fact]
    public async Task Concurrent_bearer_and_cookie_requests_never_share_an_identity()
    {
        await using var fixture = await StartAsync(enabled: true);
        var cookieReader = fixture.Cookie(ManagementPermission.Read, "cookie-reader");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(
            async () => index % 2 == 0
                ? await fixture.SendAsync(
                    HttpMethod.Put,
                    authorization: ["Bearer " + BearerHandler.AdminToken])
                : await fixture.PutAsync(cookieReader),
            Token)));
        try
        {
            Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.Forbidden));
            Assert.All(fixture.AuthorizedOperators, id => Assert.Equal("bearer-admin", id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("cookie")]
    [InlineData("bearer")]
    public async Task Caller_cancellation_propagates_exactly_as_for_the_cookie(string credential)
    {
        await using var fixture = await StartAsync(enabled: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Validator.BeforeObserving = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        };
        using var abort = new CancellationTokenSource();

        var pending = credential == "cookie"
            ? fixture.SendAsync(HttpMethod.Put, cookie: fixture.Cookie(ManagementPermission.Admin), abort: abort)
            : fixture.SendAsync(
                HttpMethod.Put,
                authorization: ["Bearer " + BearerHandler.AdminToken],
                abort: abort);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        release.TrySetResult();
        Assert.Equal(1, UpdateCalls(fixture));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("Bearer", true)]
    [InlineData("Basic x", true)]
    [InlineData("Bearer a,Bearer b", true)]
    public void Any_authorization_header_selects_the_bearer_scheme(string? header, bool bearer)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (header is not null)
        {
            context.Request.Headers.Authorization = header.Split(',');
        }

        Assert.Equal(
            bearer ? BearerHandler.SchemeName : ManagementSessionDefaults.AuthenticationScheme,
            BootstrapUpdateCredential.Select(context, BearerHandler.SchemeName));
    }

    [Theory]
    [InlineData(null, "cookie-one")]
    [InlineData("cookie-one", null)]
    [InlineData("cookie-one", "cookie-two")]
    public async Task Unselected_cookies_cannot_change_a_bearer_operators_quota(
        string? initialCookie, string? nextCookie)
    {
        await using var fixture = await StartAsync(enabled: true, managementPermitLimit: 1);
        string[] bearer = ["Bearer " + BearerHandler.AdminToken];
        using var first = await fixture.SendAsync(HttpMethod.Put,
            cookie: initialCookie is null ? null : fixture.Cookie(ManagementPermission.Read, initialCookie),
            authorization: bearer);
        using var repeat = await fixture.SendAsync(HttpMethod.Put,
            cookie: initialCookie is null ? null : fixture.Cookie(ManagementPermission.Read, initialCookie),
            authorization: bearer);
        using var changed = await fixture.SendAsync(HttpMethod.Put,
            cookie: nextCookie is null ? null : fixture.Cookie(ManagementPermission.Read, nextCookie),
            authorization: bearer);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
        Assert.Equal(["bearer-admin"], fixture.AuthorizedOperators);
        Assert.Equal(1, UpdateCalls(fixture));
    }

    [Fact]
    public async Task Different_bearer_operators_and_a_cookie_operator_have_independent_quotas()
    {
        await using var fixture = await StartAsync(enabled: true, managementPermitLimit: 1);
        var cookie = fixture.Cookie(ManagementPermission.Admin, "cookie-admin");
        foreach (var token in new[] { BearerHandler.AdminToken, BearerHandler.OtherAdminToken })
        {
            using var first = await fixture.SendAsync(HttpMethod.Put,
                cookie: cookie, authorization: ["Bearer " + token]);
            using var repeat = await fixture.SendAsync(HttpMethod.Put,
                authorization: ["Bearer " + token]);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, repeat.StatusCode);
        }

        using var cookieFirst = await fixture.PutAsync(cookie);
        using var cookieRepeat = await fixture.PutAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, cookieFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, cookieRepeat.StatusCode);
        Assert.Equal(["bearer-admin", "bearer-other-admin", "cookie-admin"], fixture.AuthorizedOperators);
    }

    [Fact]
    public async Task An_invalid_bearer_cannot_borrow_a_cookie_operators_quota()
    {
        await using var fixture = await StartAsync(enabled: true, managementPermitLimit: 1);
        var cookie = fixture.Cookie(ManagementPermission.Admin, "cookie-admin");
        using var invalid = await fixture.SendAsync(HttpMethod.Put,
            cookie: cookie, authorization: ["Bearer invalid"]);
        using var changedCookie = await fixture.SendAsync(HttpMethod.Put,
            cookie: fixture.Cookie(ManagementPermission.Admin, "another-admin"),
            authorization: ["Bearer invalid"]);
        using var cookieOnly = await fixture.PutAsync(cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(BearerHandler.UnauthenticatedCode, await ReadErrorCodeAsync(invalid));
        Assert.Equal(HttpStatusCode.TooManyRequests, changedCookie.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cookieOnly.StatusCode);
        Assert.Equal(["cookie-admin"], fixture.AuthorizedOperators);
    }

    // ---- The option widens nothing else ----

    [Fact]
    public async Task Only_the_update_entry_uses_the_credential_selector()
    {
        await using var fixture = await StartAsync(enabled: true, mapStatus: true);
        var services = fixture.Application.Services;
        var policies = services.GetRequiredService<IAuthorizationPolicyProvider>();

        var session = await policies.GetPolicyAsync(ManagementAuthorizationDefaults.SessionPolicyName);
        var admin = await policies.GetPolicyAsync(ManagementAuthorizationDefaults.AdminPolicyName);
        var update = await policies.GetPolicyAsync(BootstrapUpdateCredential.SessionPolicyName);
        Assert.Equal([ManagementSessionDefaults.AuthenticationScheme], session!.AuthenticationSchemes);
        Assert.Empty(admin!.AuthenticationSchemes);
        Assert.Equal([BootstrapUpdateCredential.SelectorScheme], update!.AuthenticationSchemes);

        var entries = services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => (
                Kind: endpoint.Metadata.GetMetadata<ManagementEntryMetadata>()?.Kind,
                Policies: endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(data => data.Policy)
                    .ToArray()))
            .Where(entry => entry.Kind is not null)
            .ToList();
        Assert.Contains(entries, entry => entry.Kind == ManagementEntryKind.BootstrapUpdate);
        Assert.All(entries, entry => Assert.Equal(
            entry.Kind == ManagementEntryKind.BootstrapUpdate,
            entry.Policies.Contains(BootstrapUpdateCredential.SessionPolicyName)));
        Assert.DoesNotContain(
            entries.Single(entry => entry.Kind == ManagementEntryKind.BootstrapUpdate).Policies,
            policy => policy == ManagementAuthorizationDefaults.SessionPolicyName);

        // The default authentication stays the cookie, so no other route authenticates a Bearer.
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(
            ManagementSessionDefaults.AuthenticationScheme,
            (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
    }

    // ---- Configuration failures ----

    public static TheoryData<string> InvalidConfigurations => new()
    {
        "unregistered",
        "blank",
        "cookie",
        "selector",
        "forwarding",
        "conflict",
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public async Task An_unusable_configuration_fails_at_startup_with_a_fixed_message(string configuration)
    {
        const string forwarding = "ServiceMantle.Tests.Forwarding";
        var scheme = configuration switch
        {
            "unregistered" => "ServiceMantle.Tests.Unregistered",
            "blank" => "  ",
            "cookie" => ManagementSessionDefaults.AuthenticationScheme,
            "selector" => BootstrapUpdateCredential.SelectorScheme,
            "forwarding" => forwarding,
            _ => BearerHandler.SchemeName,
        };
        await using var fixture = BootstrapManagementHostFixture.Create(
            snapshot: BootstrapManagementHostFixture.Ready,
            updateBearerScheme: scheme,
            registerTestBearer: true,
            configureMantle: mantle =>
            {
                if (configuration == "forwarding")
                {
                    mantle.Services.AddAuthentication().AddPolicyScheme(
                        forwarding,
                        displayName: null,
                        options => options.ForwardDefault = BearerHandler.SchemeName);
                }

                if (configuration == "conflict")
                {
                    mantle.AddServiceMantleBootstrapManagement(
                        options => options.UpdateBearerAuthenticationScheme = forwarding);
                }
            });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(fixture.StartAsync);

        Assert.Equal(BootstrapMapping.InvalidUpdateCredential().Message, failure.Message);
        Assert.DoesNotContain(scheme.Trim().Length == 0 ? "\u0000" : scheme, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceMantle.Tests", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parameterless_registration_beside_an_enabled_one_conflicts()
    {
        await using var fixture = BootstrapManagementHostFixture.Create(
            snapshot: BootstrapManagementHostFixture.Ready,
            updateBearerScheme: BearerHandler.SchemeName,
            registerTestBearer: true,
            configureMantle: mantle => mantle.AddServiceMantleBootstrapManagement());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(fixture.StartAsync);

        Assert.Equal(BootstrapMapping.InvalidUpdateCredential().Message, failure.Message);
    }

    [Fact]
    public async Task Equivalent_registrations_are_idempotent()
    {
        await using var fixture = BootstrapManagementHostFixture.Create(
            snapshot: BootstrapManagementHostFixture.BeforeConfiguration,
            updateBearerScheme: BearerHandler.SchemeName,
            registerTestBearer: true,
            configureMantle: mantle => mantle.AddServiceMantleBootstrapManagement(
                options => options.UpdateBearerAuthenticationScheme = BearerHandler.SchemeName));
        await fixture.StartAsync();
        using (var created = await fixture.PostAsync(await fixture.ProvisionAsync()))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;

        using var response = await fixture.SendAsync(
            HttpMethod.Put,
            authorization: ["Bearer " + BearerHandler.AdminToken]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void The_options_never_render_the_scheme_name()
    {
        var options = new BootstrapManagementOptions
        {
            UpdateBearerAuthenticationScheme = BearerHandler.SchemeName,
        };

        Assert.DoesNotContain(BearerHandler.SchemeName, options.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts a host whose Bootstrap file already exists - created through the anonymous creation
    /// entry - so the update entry has a file to replace, and resets the validator count.
    /// </summary>
    private static async Task<BootstrapManagementHostFixture> StartAsync(
        bool enabled, bool mapStatus = false, int managementPermitLimit = 120)
    {
        var fixture = await BootstrapManagementHostFixture.StartAsync(
            snapshot: BootstrapManagementHostFixture.BeforeConfiguration,
            mapStatus: mapStatus,
            managementPermitLimit: managementPermitLimit,
            updateBearerScheme: enabled ? BearerHandler.SchemeName : null,
            registerTestBearer: true);
        using (var created = await fixture.PostAsync(await fixture.ProvisionAsync()))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        fixture.CreatedCalls = fixture.Validator.Calls;
        return fixture;
    }

    private static int UpdateCalls(BootstrapManagementHostFixture fixture) =>
        fixture.Validator.Calls - fixture.CreatedCalls;

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
}

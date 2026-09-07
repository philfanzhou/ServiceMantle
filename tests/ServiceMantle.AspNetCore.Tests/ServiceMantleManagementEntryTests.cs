using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the shared management entry baseline: fixed path and method per kind, method-aware phase
/// admission, the fixed authentication and rate-limit rules, the unsafe-request guard, and the
/// startup checks that reject a downgraded entry.
/// </summary>
public sealed class ServiceMantleManagementEntryTests
{
    private const string PhaseUnavailable = "service.phase.unavailable";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<ServiceStartupPhase, ServiceMigrationReadinessState, ServiceDatabaseReadinessState> PhaseMatrix
    {
        get
        {
            var data = new TheoryData<
                ServiceStartupPhase,
                ServiceMigrationReadinessState,
                ServiceDatabaseReadinessState>();
            foreach (var phase in Enum.GetValues<ServiceStartupPhase>())
            {
                foreach (var migration in Enum.GetValues<ServiceMigrationReadinessState>())
                {
                    foreach (var database in Enum.GetValues<ServiceDatabaseReadinessState>())
                    {
                        data.Add(phase, migration, database);
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<ServiceMantleManagementEntryKind> Kinds
    {
        get
        {
            var data = new TheoryData<ServiceMantleManagementEntryKind>();
            foreach (var kind in ManagementEntryHostFixture.AllKinds)
            {
                data.Add(kind);
            }

            return data;
        }
    }

    /// <summary>
    /// The admitted states, written out per entry instead of derived from the implementation.
    /// </summary>
    private static bool IsAdmitted(
        ServiceMantleManagementEntryKind kind,
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var ready = phase == ServiceStartupPhase.Completed &&
            migration == ServiceMigrationReadinessState.Succeeded &&
            database == ServiceDatabaseReadinessState.Reachable;
        return kind switch
        {
            ServiceMantleManagementEntryKind.InstallationStatus => true,
            ServiceMantleManagementEntryKind.BootstrapCreate =>
                phase == ServiceStartupPhase.BootstrapConfiguration &&
                migration is ServiceMigrationReadinessState.NotStarted or ServiceMigrationReadinessState.Succeeded,
            ServiceMantleManagementEntryKind.SetupStatus or ServiceMantleManagementEntryKind.SetupComplete =>
                (phase == ServiceStartupPhase.PendingSetup || phase == ServiceStartupPhase.Completed) &&
                migration == ServiceMigrationReadinessState.Succeeded &&
                database == ServiceDatabaseReadinessState.Reachable,
            _ => ready,
        };
    }

    [Theory]
    [MemberData(nameof(PhaseMatrix))]
    public async Task Every_entry_follows_its_own_phase_and_method_admission(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(
            new ServiceHealthSnapshot(phase, migration, database));
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        foreach (var kind in ManagementEntryHostFixture.AllKinds)
        {
            using var response = await fixture.SendAsync(kind, cookie);
            var admitted = IsAdmitted(kind, phase, migration, database);
            Assert.Equal(
                admitted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                response.StatusCode);
            Assert.Equal(admitted ? 1 : 0, fixture.Recorder.Calls(kind));
            if (!admitted)
            {
                Assert.Equal(PhaseUnavailable, await ReadErrorCodeAsync(response));
            }
        }

        // Installation status is the only entry the gate never reads a snapshot for.
        Assert.Equal(ManagementEntryHostFixture.AllKinds.Length - 1, source.Calls);
    }

    [Fact]
    public async Task Anonymous_bootstrap_create_never_runs_once_the_service_is_configured()
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);

        using var response = await fixture.SendAsync(ServiceMantleManagementEntryKind.BootstrapCreate);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(PhaseUnavailable, await ReadErrorCodeAsync(response));
        Assert.Equal(0, fixture.Recorder.Total);
    }

    [Fact]
    public async Task Administrator_bootstrap_update_never_runs_before_configuration()
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(new ServiceHealthSnapshot(
            ServiceStartupPhase.BootstrapConfiguration,
            ServiceMigrationReadinessState.NotStarted,
            ServiceDatabaseReadinessState.Unreachable));
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);

        using var response = await fixture.SendAsync(
            ServiceMantleManagementEntryKind.BootstrapUpdate,
            fixture.Cookie(ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(PhaseUnavailable, await ReadErrorCodeAsync(response));
        Assert.Equal(0, fixture.Recorder.Total);
    }

    [Theory]
    [InlineData(ServiceMantleManagementEntryKind.SetupStatus)]
    [InlineData(ServiceMantleManagementEntryKind.SetupComplete)]
    public async Task Setup_replay_reaches_the_handler_after_completion(ServiceMantleManagementEntryKind kind)
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);

        using var response = await fixture.SendAsync(kind);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Recorder.Calls(kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Each_kind_only_accepts_its_fixed_path_and_method(ServiceMantleManagementEntryKind kind)
    {
        var phase = kind == ServiceMantleManagementEntryKind.BootstrapCreate
            ? ServiceStartupPhase.BootstrapConfiguration
            : ServiceStartupPhase.Completed;
        var migration = ServiceMigrationReadinessState.Succeeded;
        var database = ServiceDatabaseReadinessState.Reachable;
        await using var fixture = await ManagementEntryHostFixture.StartAsync(
            new ManagementEntryHostFixture.SnapshotSource(
                new ServiceHealthSnapshot(phase, migration, database)));
        var cookie = fixture.Cookie(ManagementPermission.Admin);
        var path = fixture.Path(kind);
        var admittedOnThisPath = ManagementEntryHostFixture.AllKinds.Count(candidate =>
            fixture.Path(candidate) == path && IsAdmitted(candidate, phase, migration, database));

        foreach (var method in new[]
        {
            HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch,
        })
        {
            // Only the kind that owns this exact path and method may run, and only when its own
            // phase rule admits it. Every other method is stopped before any handler.
            var owner = ManagementEntryHostFixture.AllKinds
                .Where(candidate => fixture.Path(candidate) == path &&
                    ManagementEntryHostFixture.Method(candidate) == method)
                .Cast<ServiceMantleManagementEntryKind?>()
                .SingleOrDefault();
            var expected = owner is not null && IsAdmitted(owner.Value, phase, migration, database);
            using var response = await fixture.SendAsync(kind, cookie, method: method, path: path);
            Assert.Equal(
                expected ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                response.StatusCode);
        }

        Assert.Equal(1, fixture.Recorder.Calls(kind));
        Assert.Equal(admittedOnThisPath, fixture.Recorder.Total);

        // A look-alike sibling is not the entry: it is not routed at all.
        using var lookAlike = await fixture.SendAsync(
            kind,
            cookie,
            method: ManagementEntryHostFixture.Method(kind),
            path: path + "-other");
        using var nested = await fixture.SendAsync(
            kind,
            cookie,
            method: ManagementEntryHostFixture.Method(kind),
            path: path + "/nested");

        Assert.Equal(HttpStatusCode.NotFound, lookAlike.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nested.StatusCode);
        Assert.Equal(admittedOnThisPath, fixture.Recorder.Total);
    }

    [Fact]
    public async Task Head_is_routed_for_every_read_entry()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        foreach (var kind in new[]
        {
            ServiceMantleManagementEntryKind.InstallationStatus,
            ServiceMantleManagementEntryKind.SetupStatus,
            ServiceMantleManagementEntryKind.CurrentSession,
        })
        {
            using var response = await fixture.SendAsync(kind, cookie, method: HttpMethod.Head);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, fixture.Recorder.Calls(kind));
        }
    }

    [Theory]
    [InlineData(ServiceMantleManagementEntryKind.BootstrapCreate)]
    [InlineData(ServiceMantleManagementEntryKind.SetupComplete)]
    [InlineData(ServiceMantleManagementEntryKind.SessionLogin)]
    [InlineData(ServiceMantleManagementEntryKind.SessionLogout)]
    [InlineData(ServiceMantleManagementEntryKind.BootstrapUpdate)]
    public async Task Unsafe_entries_require_exactly_one_fixed_request_header(
        ServiceMantleManagementEntryKind kind)
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(
            kind == ServiceMantleManagementEntryKind.BootstrapCreate
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.BootstrapConfiguration,
                    ServiceMigrationReadinessState.NotStarted,
                    ServiceDatabaseReadinessState.Unreachable)
                : ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        foreach (var header in new string?[]?[]
        {
            [],
            [""],
            ["0"],
            ["true"],
            ["1", "1"],
            ["1,1"],
            [" 1"],
        })
        {
            using var rejected = await fixture.SendAsync(kind, cookie, unsafeHeader: header);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(
                "application/problem+json",
                rejected.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                ServiceMantleManagementApiDefaults.InvalidRequestErrorCode,
                await ReadErrorCodeAsync(rejected));
            Assert.Equal(0, fixture.Recorder.Total);
        }

        using var accepted = await fixture.SendAsync(kind, cookie);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, fixture.Recorder.Calls(kind));
    }

    [Theory]
    [InlineData(ServiceMantleManagementEntryKind.InstallationStatus)]
    [InlineData(ServiceMantleManagementEntryKind.SetupStatus)]
    [InlineData(ServiceMantleManagementEntryKind.CurrentSession)]
    public async Task Read_entries_never_depend_on_the_unsafe_request_header(
        ServiceMantleManagementEntryKind kind)
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync();
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        using var withoutHeader = await fixture.SendAsync(kind, cookie, unsafeHeader: []);
        using var withWrongHeader = await fixture.SendAsync(kind, cookie, unsafeHeader: ["nonsense"]);

        Assert.Equal(HttpStatusCode.OK, withoutHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withWrongHeader.StatusCode);
        Assert.Equal(2, fixture.Recorder.Calls(kind));
    }

    [Theory]
    [InlineData(ServiceMantleManagementEntryKind.InstallationStatus)]
    [InlineData(ServiceMantleManagementEntryKind.BootstrapCreate)]
    [InlineData(ServiceMantleManagementEntryKind.SetupStatus)]
    [InlineData(ServiceMantleManagementEntryKind.SetupComplete)]
    [InlineData(ServiceMantleManagementEntryKind.SessionLogin)]
    public async Task Anonymous_entries_run_without_any_cookie(ServiceMantleManagementEntryKind kind)
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(
            kind == ServiceMantleManagementEntryKind.BootstrapCreate
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.BootstrapConfiguration,
                    ServiceMigrationReadinessState.NotStarted,
                    ServiceDatabaseReadinessState.Unreachable)
                : ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);

        using var response = await fixture.SendAsync(kind);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Recorder.Calls(kind));
    }

    [Theory]
    [InlineData(ServiceMantleManagementEntryKind.BootstrapUpdate)]
    [InlineData(ServiceMantleManagementEntryKind.SessionLogout)]
    [InlineData(ServiceMantleManagementEntryKind.CurrentSession)]
    public async Task Protected_entries_keep_the_existing_cookie_results(
        ServiceMantleManagementEntryKind kind)
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync();

        using var missing = await fixture.SendAsync(kind);
        using var expired = await fixture.SendAsync(
            kind,
            fixture.Cookie(ManagementPermission.Admin, age: TimeSpan.FromHours(2)));
        using var invalidClaims = await fixture.SendAsync(kind, fixture.CookieWithInvalidClaims());

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode,
            await ReadErrorCodeAsync(missing));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ExpiredErrorCode,
            await ReadErrorCodeAsync(expired));
        Assert.Equal(HttpStatusCode.Forbidden, invalidClaims.StatusCode);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ForbiddenErrorCode,
            await ReadErrorCodeAsync(invalidClaims));
        Assert.Equal(0, fixture.Recorder.Total);
    }

    [Fact]
    public async Task Session_entries_accept_a_legitimate_identity_without_admin()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync();
        var reader = fixture.Cookie(ManagementPermission.Read);

        using var logout = await fixture.SendAsync(ServiceMantleManagementEntryKind.SessionLogout, reader);
        using var current = await fixture.SendAsync(ServiceMantleManagementEntryKind.CurrentSession, reader);
        using var update = await fixture.SendAsync(ServiceMantleManagementEntryKind.BootstrapUpdate, reader);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        // The same identity is still rejected by the administrator-only Bootstrap update.
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(0, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.BootstrapUpdate));
    }

    [Fact]
    public async Task Installation_status_stays_on_the_anonymous_client_partition()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync(managementPermitLimit: 4);
        var first = fixture.Cookie(ManagementPermission.Admin, "operator-1");
        var second = fixture.Cookie(ManagementPermission.Admin, "operator-2");

        // Two operators and one anonymous caller share one client quota on the status entry.
        var statuses = new List<HttpStatusCode>();
        foreach (var cookie in new[] { null, first, second, first, second })
        {
            using var response = await fixture.SendAsync(
                ServiceMantleManagementEntryKind.InstallationStatus,
                cookie);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(4, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(4, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.InstallationStatus));
    }

    [Fact]
    public async Task Other_management_entries_keep_the_per_operator_partition()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync(managementPermitLimit: 2);
        var first = fixture.Cookie(ManagementPermission.Read, "operator-1");
        var second = fixture.Cookie(ManagementPermission.Read, "operator-2");

        var firstStatuses = new List<HttpStatusCode>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            using var response = await fixture.SendAsync(
                ServiceMantleManagementEntryKind.CurrentSession,
                first);
            firstStatuses.Add(response.StatusCode);
        }

        using var otherOperator = await fixture.SendAsync(
            ServiceMantleManagementEntryKind.CurrentSession,
            second);

        Assert.Equal(2, firstStatuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, firstStatuses[^1]);
        // A second operator on the same client address keeps its own quota.
        Assert.Equal(HttpStatusCode.OK, otherOperator.StatusCode);
    }

    [Fact]
    public async Task Setup_partitioned_entries_share_the_setup_policy_quota()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync(setupPermitLimit: 2);

        using var first = await fixture.SendAsync(ServiceMantleManagementEntryKind.SetupStatus);
        using var second = await fixture.SendAsync(ServiceMantleManagementEntryKind.SetupComplete);
        using var third = await fixture.SendAsync(ServiceMantleManagementEntryKind.SessionLogin);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(0, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.SessionLogin));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Every_entry_carries_the_security_headers_and_the_correlation_id(
        ServiceMantleManagementEntryKind kind)
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(
            kind == ServiceMantleManagementEntryKind.BootstrapCreate
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.BootstrapConfiguration,
                    ServiceMigrationReadinessState.NotStarted,
                    ServiceDatabaseReadinessState.Unreachable)
                : ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);

        using var response = await fixture.SendAsync(kind, fixture.Cookie(ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
        })
        {
            Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
        }

        Assert.NotEmpty(Assert.Single(response.Headers.GetValues("x-correlation-id")));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_never_becomes_a_fixed_error()
    {
        await using var fixture = await ManagementEntryHostFixture.StartAsync();
        var abort = fixture.CreateAbort();
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.SendAsync(ServiceMantleManagementEntryKind.InstallationStatus, abort: abort));

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, fixture.Recorder.Total);
    }

    [Fact]
    public async Task Concurrent_entry_requests_stay_isolated()
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(source);
        var admin = fixture.Cookie(ManagementPermission.Admin, "operator-admin");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var authenticated = index % 2 == 0;
            using var response = await fixture.SendAsync(
                authenticated
                    ? ServiceMantleManagementEntryKind.CurrentSession
                    : ServiceMantleManagementEntryKind.SetupStatus,
                authenticated ? admin : null);
            return response.StatusCode;
        }));

        Assert.All(results, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(6, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.CurrentSession));
        Assert.Equal(6, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.SetupStatus));
        // One Gate observation per admitted request and never more.
        Assert.Equal(12, source.Calls);
    }

    [Fact]
    public async Task A_phase_change_after_admission_does_not_recall_the_request()
    {
        var source = new ManagementEntryHostFixture.SnapshotSource(ManagementEntryHostFixture.Ready);
        await using var fixture = await ManagementEntryHostFixture.StartAsync(
            source,
            map: [ServiceMantleManagementEntryKind.CurrentSession]);
        fixture.Recorder.Hold = ServiceMantleManagementEntryKind.CurrentSession;
        var request = fixture.SendAsync(
            ServiceMantleManagementEntryKind.CurrentSession,
            fixture.Cookie(ManagementPermission.Admin));
        await fixture.Recorder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        source.Current = new ServiceHealthSnapshot(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Failed,
            ServiceDatabaseReadinessState.Unreachable);
        fixture.Recorder.Released.TrySetResult();
        using var response = await request;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Recorder.Calls(ServiceMantleManagementEntryKind.CurrentSession));
    }

    [Fact]
    public async Task Mapping_requires_the_management_api_root_and_the_entry_capability()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagementEntryHostFixture.CreateAsync(entries: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagementEntryHostFixture.CreateAsync(managementApi: false));
    }

    [Fact]
    public async Task Mapping_rejects_an_undefined_entry_kind()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var fixture = await ManagementEntryHostFixture.CreateAsync(
                map: [(ServiceMantleManagementEntryKind)999]);
        });
    }

    [Theory]
    [InlineData("anonymous-downgrade")]
    [InlineData("disabled-rate-limiting")]
    [InlineData("replaced-rate-limiting")]
    [InlineData("second-surface")]
    [InlineData("duplicate-kind")]
    [InlineData("inside-protected-group")]
    [InlineData("no-security-headers")]
    [InlineData("no-rate-limiting")]
    [InlineData("no-pipeline")]
    [InlineData("no-cookie-authentication")]
    public async Task A_downgraded_or_incomplete_entry_fails_before_the_host_starts(string scenario)
    {
        // A missing capability may already stop the pipeline composition; both exits are startup
        // failures and neither may leave a usable entry surface behind.
        ManagementEntryHostFixture? fixture = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            fixture = await CreateForFailureAsync(scenario);
            await fixture.StartAsync();
        });

        if (fixture is not null)
        {
            await fixture.DisposeAsync();
        }

        Assert.DoesNotContain("operator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_protected_group_still_rejects_a_forged_anonymous_exception()
    {
        await using var fixture = await ManagementApiHostFixture.CreateAsync(
            children: group => group.MapGet("/forged", () => Results.Ok()).AllowAnonymous());

        await Assert.ThrowsAsync<InvalidOperationException>(fixture.StartAsync);
    }

    private static async Task<ManagementEntryHostFixture> CreateForFailureAsync(string scenario) => scenario switch
    {
        "anonymous-downgrade" => await ManagementEntryHostFixture.CreateAsync(
            map: [ServiceMantleManagementEntryKind.BootstrapUpdate],
            configureEntry: (_, entry) => entry.AllowAnonymous()),
        "disabled-rate-limiting" => await ManagementEntryHostFixture.CreateAsync(
            map: [ServiceMantleManagementEntryKind.InstallationStatus],
            configureEntry: (_, entry) => entry.DisableRateLimiting()),
        "replaced-rate-limiting" => await ManagementEntryHostFixture.CreateAsync(
            map: [ServiceMantleManagementEntryKind.BootstrapUpdate],
            configureEntry: (_, entry) => entry.RequireRateLimiting(
                ServiceMantleRateLimitingDefaults.SetupPolicyName)),
        "second-surface" => await ManagementEntryHostFixture.CreateAsync(
            map: [ServiceMantleManagementEntryKind.SetupStatus],
            configureEntry: (_, entry) => entry.WithServiceMantleManagementSurface(
                ServiceMantleManagementSurface.Management)),
        "duplicate-kind" => await ManagementEntryHostFixture.CreateAsync(
            map:
            [
                ServiceMantleManagementEntryKind.SessionLogin,
                ServiceMantleManagementEntryKind.SessionLogin,
            ]),
        "inside-protected-group" => await ManagementEntryHostFixture.CreateAsync(
            map: [ServiceMantleManagementEntryKind.InstallationStatus],
            extra: (application, _) => application.MapServiceMantleManagementApiV1()
                .MapServiceMantleManagementEntry(
                    ServiceMantleManagementEntryKind.SetupStatus,
                    () => Results.Ok())),
        "no-security-headers" => await ManagementEntryHostFixture.CreateAsync(securityHeaders: false),
        "no-rate-limiting" => await ManagementEntryHostFixture.CreateAsync(rateLimiting: false),
        "no-pipeline" => await ManagementEntryHostFixture.CreateAsync(composition: "manual"),
        _ => await ManagementEntryHostFixture.CreateAsync(cookieAuthentication: false),
    };

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.TryGetProperty("errorCode", out var errorCode)
            ? errorCode.GetString()
            : null;
    }
}

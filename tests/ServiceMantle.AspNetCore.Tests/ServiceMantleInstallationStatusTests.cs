using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ServiceMantle.Health;
using ServiceMantle.Http;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

using Fixture = InstallationStatusHostFixture;

public sealed class ServiceMantleInstallationStatusTests
{
    private const string Unavailable = "management.status.unavailable";

    [Fact]
    public async Task The_entry_is_opt_in_and_serves_nothing_when_it_is_not_mapped()
    {
        await using var fixture = await Fixture.StartAsync(
            extra: null,
            source: new Fixture.CountingSnapshotSource(
                Fixture.Snapshot(ServiceStartupPhase.BootstrapConfiguration)));
        await using var unmapped = await Fixture.CreateAsync(mapCount: 0);
        await unmapped.StartAsync();

        using var response = await unmapped.SendAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync()).StatusCode);
    }

    [Fact]
    public async Task Mapping_twice_or_without_the_capability_fails_before_the_host_starts()
    {
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(mapCount: 2));
        var withoutCapability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(status: false, entries: false));
        var withoutManagementApi = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(status: false, managementApi: false));

        Assert.DoesNotContain(
            ServiceMantleManagementApiDefaults.DefaultRootPath,
            duplicate.Message,
            StringComparison.Ordinal);
        Assert.NotNull(withoutCapability.Message);
        Assert.NotNull(withoutManagementApi.Message);
    }

    [Theory]
    [InlineData(ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Reachable, "not_started", "reachable")]
    [InlineData(ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Unreachable, "running", "unreachable")]
    [InlineData(ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable, "succeeded", "reachable")]
    [InlineData(ServiceMigrationReadinessState.Failed, ServiceDatabaseReadinessState.Unreachable, "failed", "unreachable")]
    public async Task Every_migration_and_database_state_is_projected_in_fixed_lower_snake_case(
        ServiceMigrationReadinessState migrationStatus,
        ServiceDatabaseReadinessState databaseStatus,
        string expectedMigration,
        string expectedDatabase)
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(
                Fixture.Snapshot(ServiceStartupPhase.Completed, migrationStatus, databaseStatus)),
            bootstrapFile: "valid");

        using var response = await fixture.SendAsync();
        var body = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertSuccessShape(body);
        Assert.Equal("completed", body.GetProperty("phase").GetString());
        Assert.Equal(expectedMigration, body.GetProperty("migrationStatus").GetString());
        Assert.Equal(expectedDatabase, body.GetProperty("databaseStatus").GetString());
        Assert.True(body.GetProperty("bootstrapConfigured").GetBoolean());
        Assert.False(body.GetProperty("restartRequired").GetBoolean());
    }

    [Theory]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, "absent", false, "bootstrap_configuration", false, false)]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, "valid", true, "bootstrap_configuration", true, true)]
    [InlineData(ServiceStartupPhase.PendingSetup, "valid", false, "pending_setup", true, false)]
    [InlineData(ServiceStartupPhase.PendingSetup, "valid", true, "pending_setup", true, true)]
    [InlineData(ServiceStartupPhase.Completed, "valid", true, "completed", true, true)]
    public async Task Coherent_phase_and_bootstrap_combinations_are_answered(
        ServiceStartupPhase phase,
        string bootstrapFile,
        bool latched,
        string expectedPhase,
        bool expectedConfigured,
        bool expectedRestart)
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(phase)),
            bootstrapFile: bootstrapFile);
        if (latched)
        {
            fixture.Latch.Latch();
        }

        using var response = await fixture.SendAsync();
        var body = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSuccessShape(body);
        Assert.Equal(expectedPhase, body.GetProperty("phase").GetString());
        Assert.Equal(expectedConfigured, body.GetProperty("bootstrapConfigured").GetBoolean());
        Assert.Equal(expectedRestart, body.GetProperty("restartRequired").GetBoolean());
    }

    [Theory]
    // A Bootstrap file exists before configuration although this process never wrote one.
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, "valid", false)]
    // The phase moved past configuration without a valid local Bootstrap file.
    [InlineData(ServiceStartupPhase.PendingSetup, "absent", false)]
    [InlineData(ServiceStartupPhase.PendingSetup, "absent", true)]
    [InlineData(ServiceStartupPhase.Completed, "absent", false)]
    [InlineData(ServiceStartupPhase.Completed, "absent", true)]
    public async Task Combinations_the_two_sources_contradict_are_unavailable(
        ServiceStartupPhase phase,
        string bootstrapFile,
        bool latched)
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(phase)),
            bootstrapFile: bootstrapFile);
        if (latched)
        {
            fixture.Latch.Latch();
        }

        using var response = await fixture.SendAsync();

        await AssertUnavailableAsync(response);
    }

    [Fact]
    public async Task A_damaged_bootstrap_file_is_unavailable_and_never_reported_as_absent()
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(
                Fixture.Snapshot(ServiceStartupPhase.BootstrapConfiguration)),
            bootstrapFile: "damaged");

        using var response = await fixture.SendAsync();

        await AssertUnavailableAsync(response);
    }

    [Fact]
    public async Task A_missing_or_failing_snapshot_source_is_unavailable()
    {
        const string secret = "Server=db.internal;Password=snapshot-secret";
        await using var missing = await Fixture.CreateAsync(source: null, bootstrapFile: "valid");
        await missing.StartAsync();
        await using var failing = await Fixture.StartAsync(
            source: new Fixture.FailingSnapshotSource(new InvalidOperationException(secret)),
            bootstrapFile: "valid");

        using var withoutSource = await missing.SendAsync();
        using var withFailure = await failing.SendAsync();

        await AssertUnavailableAsync(withoutSource);
        var body = await AssertUnavailableAsync(withFailure);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_timeout_is_unavailable_and_is_not_a_caller_cancellation()
    {
        var source = new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed))
        {
            // Never released: only the entry's own budget can end this request.
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await Fixture.StartAsync(
            source: source,
            bootstrapFile: "valid",
            snapshotTimeout: TimeSpan.FromMilliseconds(100));

        using var response = await fixture.SendAsync();

        await AssertUnavailableAsync(response);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_fixed_result()
    {
        var source = new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed))
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var fixture = await Fixture.StartAsync(
            source: source,
            bootstrapFile: "valid",
            snapshotTimeout: TimeSpan.FromSeconds(30));
        var abort = fixture.CreateAbort();

        var request = fixture.SendAsync(abort: abort);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Each_source_is_read_exactly_once_and_the_gate_reads_no_snapshot()
    {
        var source = new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed));
        var bootstrap = new Fixture.CountingBootstrapStatusReader(configured: true);
        await using var fixture = await Fixture.StartAsync(source: source, bootstrapReader: bootstrap);

        using var response = await fixture.SendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // One read each: the Phase Gate admits this surface without observing a snapshot, so the
        // handler's own read is the only one in the request.
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, bootstrap.Calls);
    }

    [Fact]
    public async Task A_phase_change_after_the_single_read_does_not_reshape_the_answer()
    {
        // Two different snapshots per call would mix inside one response if the handler read twice.
        var source = new Fixture.SequenceSnapshotSource(
            Fixture.Snapshot(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded),
            Fixture.Snapshot(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.NotStarted));
        await using var fixture = await Fixture.StartAsync(source: source, bootstrapFile: "valid");

        using var first = await fixture.SendAsync();
        using var second = await fixture.SendAsync();
        var firstBody = await ReadBodyAsync(first);
        var secondBody = await ReadBodyAsync(second);

        Assert.Equal("completed", firstBody.GetProperty("phase").GetString());
        Assert.Equal("succeeded", firstBody.GetProperty("migrationStatus").GetString());
        Assert.Equal("pending_setup", secondBody.GetProperty("phase").GetString());
        Assert.Equal("not_started", secondBody.GetProperty("migrationStatus").GetString());
    }

    [Fact]
    public async Task Concurrent_callers_each_observe_one_complete_projection()
    {
        var source = new Fixture.SequenceSnapshotSource(
            Fixture.Snapshot(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded),
            Fixture.Snapshot(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.NotStarted));
        await using var fixture = await Fixture.StartAsync(source: source, bootstrapFile: "valid");

        var responses = await Task.WhenAll(Enumerable
            .Range(0, 20)
            .Select(_ => fixture.SendAsync()));
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            response.Dispose();
        }

        // Every body is one of the two whole projections; no response mixes the two samples.
        const string completed =
            "{\"phase\":\"completed\",\"migrationStatus\":\"succeeded\",\"databaseStatus\":\"reachable\"," +
            "\"bootstrapConfigured\":true,\"restartRequired\":false}";
        const string pending =
            "{\"phase\":\"pending_setup\",\"migrationStatus\":\"not_started\",\"databaseStatus\":\"reachable\"," +
            "\"bootstrapConfigured\":true,\"restartRequired\":false}";
        Assert.All(bodies, body => Assert.True(body == completed || body == pending, body));
        Assert.Contains(completed, bodies);
        Assert.Contains(pending, bodies);
    }

    [Fact]
    public async Task Head_answers_the_same_status_and_headers_without_a_body()
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed)),
            bootstrapFile: "valid");

        using var get = await fixture.SendAsync();
        using var head = await fixture.SendAsync(HttpMethod.Head);
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

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Other_methods_never_reach_the_handler(string method)
    {
        var source = new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed));
        await using var fixture = await Fixture.StartAsync(source: source, bootstrapFile: "valid");

        using var response = await fixture.SendAsync(new HttpMethod(method));

        // The shared entry baseline stops a method this entry does not own at the Phase Gate; the
        // status handler is never entered and reads nothing.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_custom_versioned_root_moves_the_entry()
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed)),
            root: "/ops/admin/v1",
            bootstrapFile: "valid");

        using var moved = await fixture.SendAsync(path: "/ops/admin/v1/status");
        using var defaultRoot = await fixture.SendAsync(path: "/management/v1/status");

        Assert.Equal("/ops/admin/v1", fixture.Root);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, defaultRoot.StatusCode);
    }

    [Fact]
    public async Task The_entry_is_anonymous_but_still_rate_limited()
    {
        var source = new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed));
        await using var fixture = await Fixture.StartAsync(
            source: source,
            bootstrapFile: "valid",
            managementPermitLimit: 2);

        using var first = await fixture.SendAsync();
        using var second = await fixture.SendAsync();
        using var third = await fixture.SendAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        // The rejected request never reached the handler, so it read no source.
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Security_headers_and_the_correlation_contract_apply_to_both_outcomes()
    {
        await using var success = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed)),
            bootstrapFile: "valid");
        await using var failure = await Fixture.StartAsync(
            source: new Fixture.FailingSnapshotSource(new InvalidOperationException("boom")),
            bootstrapFile: "valid");

        using var ok = await success.SendAsync();
        using var unavailable = await failure.SendAsync();

        foreach (var response in new[] { ok, unavailable })
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.Single(response.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId))));
        }
    }

    [Fact]
    public async Task No_bootstrap_secret_or_identity_value_reaches_the_response()
    {
        await using var fixture = await Fixture.StartAsync(
            source: new Fixture.CountingSnapshotSource(Fixture.Snapshot(ServiceStartupPhase.Completed)),
            bootstrapFile: "valid");

        using var response = await fixture.SendAsync();
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
            .Select(header => header.Key + ": " + string.Join(',', header.Value)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var text in new[] { raw, headers })
        {
            foreach (var secret in new[]
            {
                Fixture.SentinelProvider,
                Fixture.SentinelServerVersion,
                Fixture.SentinelConnectionString,
                Fixture.SentinelMasterKey,
                "sentinel-connection-secret",
                "catalog-01",
                fixture.BootstrapFilePath,
            })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_restart_latch_starts_false_and_only_moves_forward()
    {
        var latch = new ServiceMantleBootstrapRestartLatch();

        Assert.False(latch.RestartRequired);
        latch.Latch();
        latch.Latch();

        Assert.True(latch.RestartRequired);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    private static void AssertSuccessShape(JsonElement body)
    {
        Assert.Equal(
            ["phase", "migrationStatus", "databaseStatus", "bootstrapConfigured", "restartRequired"],
            body.EnumerateObject().Select(property => property.Name));
    }

    private static async Task<string> AssertUnavailableAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"errorCode\":\"" + Unavailable + "\"}", raw);
        return raw;
    }
}

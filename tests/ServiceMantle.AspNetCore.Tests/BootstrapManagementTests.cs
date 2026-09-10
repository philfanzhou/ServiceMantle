using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Logging;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Status;
using ServiceMantle.Bootstrap;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the Bootstrap management entries: their opt-in mapping and prerequisites, the one-time
/// credential that authorizes a first creation, the fixed projection of every core failure, and the
/// restart latch a published file sets.
/// </summary>
public sealed class BootstrapManagementTests
{
    private const string CredentialHeader = "X-ServiceMantle-Bootstrap-Credential";
    private const string CredentialInvalid = "management.bootstrap.credential_invalid";
    private const string Unavailable = "management.bootstrap.unavailable";
    private const string InvalidRequest = "management.request.invalid";
    private const string Conflict = "management.request.conflict";
    private const string PhaseUnavailable = "service.phase.unavailable";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("/mgmt/v1")]
    public async Task The_group_maps_both_direct_children_under_either_root(string? root)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(root: root);
        var credential = await fixture.ProvisionAsync();

        using var created = await fixture.PostAsync(credential);
        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        using var updated = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Admin));
        using var elsewhere = await fixture.SendAsync(
            HttpMethod.Post,
            path: fixture.EntryPath + "/extra",
            credential: [credential]);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("""{"restartRequired":true}""", await created.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("""{"restartRequired":true}""", await updated.Content.ReadAsStringAsync(Token));
        // The entries are direct children of the versioned root and nothing below them is served.
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public void A_second_mapping_of_the_group_fails()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BootstrapManagementHostFixture.Create(mapCount: 2));

        Assert.Contains("at most once", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_credential_store_fails_before_the_host_starts()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BootstrapManagementHostFixture.Create(credentialStore: false));

        Assert.Contains("IBootstrapCredentialStore", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_cookie_scheme_fails_before_the_host_starts()
    {
        await using var fixture = BootstrapManagementHostFixture.Create(
            cookieAuthentication: false,
            externalDefaultScheme: true);

        await Assert.ThrowsAsync<InvalidOperationException>(fixture.StartAsync);
    }

    [Fact]
    public async Task The_wrong_phase_and_a_cookieless_external_admin_reach_no_handler()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(
            externalDefaultScheme: true,
            snapshot: BootstrapManagementHostFixture.Ready);
        var credential = await fixture.ProvisionAsync();

        // POST is admitted only before configuration.
        using var earlyPost = await fixture.PostAsync(credential);
        // The external handler authenticates a management administrator and is the default scheme.
        using var externalPut = await fixture.PutAsync(cookie: null);
        fixture.Snapshot.Current = BootstrapManagementHostFixture.BeforeConfiguration;
        using var latePut = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Admin));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, earlyPost.StatusCode);
        Assert.Equal(PhaseUnavailable, await ErrorCodeAsync(earlyPost));
        Assert.Equal(HttpStatusCode.Unauthorized, externalPut.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, latePut.StatusCode);
        Assert.Equal(PhaseUnavailable, await ErrorCodeAsync(latePut));
        Assert.Equal(0, fixture.Validator.Calls);
        Assert.False(File.Exists(fixture.BootstrapPath));
    }

    [Fact]
    public async Task A_read_only_operator_never_updates_the_file()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(
            snapshot: BootstrapManagementHostFixture.Ready);

        using var response = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Read));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Validator.Calls);
        Assert.False(File.Exists(fixture.BootstrapPath));
    }

    [Fact]
    public async Task A_created_file_is_complete_readable_and_latches_the_restart()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(mapStatus: true);
        var credential = await fixture.ProvisionAsync();
        var latch = fixture.Application.Services
            .GetRequiredService<BootstrapRestartLatch>();

        using var before = await fixture.Client.GetAsync(
            fixture.Root + ManagementEntryDefaults.StatusPath,
            Token);
        var latchedBefore = latch.RestartRequired;
        using var created = await fixture.PostAsync(credential);
        using var after = await fixture.Client.GetAsync(
            fixture.Root + ManagementEntryDefaults.StatusPath,
            Token);

        Assert.False(latchedBefore);
        Assert.DoesNotContain("\"restartRequired\":true", await before.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(latch.RestartRequired);
        Assert.Contains("\"restartRequired\":true", await after.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // A new store reads the published file in full.
        var loaded = new BootstrapFileStore(
            ServiceId.Parse("catalog"),
            new BootstrapDatabaseProviderRegistry([]),
            fixture.BootstrapPath).Load();
        Assert.Equal("PostgreSQL", loaded.Database.Provider);
        Assert.Equal(BootstrapManagementHostFixture.ConnectionString, loaded.Database.ConnectionString);
        Assert.Equal(BootstrapManagementHostFixture.MasterKey, loaded.MasterKey);
        Assert.Null(loaded.Database.ServerVersion);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-length")]
    [InlineData("not-provisioned")]
    [InlineData("mismatched")]
    [InlineData("repeated")]
    [InlineData("comma-combined")]
    public async Task An_unusable_credential_is_one_rejection_and_consumes_nothing(string scenario)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = scenario == "not-provisioned" ? null : await fixture.ProvisionAsync();
        string?[] header = scenario switch
        {
            "missing" => [],
            "malformed" => ["not a credential"],
            "wrong-length" => [new string('a', 42)],
            "not-provisioned" => [new string('a', 43)],
            "mismatched" => [BootstrapCredential.Generate().Reveal()],
            "repeated" => [credential, credential],
            _ => [credential + "," + credential],
        };

        using var response = await fixture.SendAsync(HttpMethod.Post, credential: header);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(CredentialInvalid, await ErrorCodeAsync(response));
        Assert.Equal(0, fixture.Validator.Calls);
        Assert.False(File.Exists(fixture.BootstrapPath));

        if (credential is not null)
        {
            // The valid credential was never spent by any of these.
            using var accepted = await fixture.PostAsync(credential);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task An_already_consumed_credential_is_the_same_rejection()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var first = await fixture.PostAsync(credential);
        File.Delete(fixture.BootstrapPath);
        using var replay = await fixture.PostAsync(credential);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(CredentialInvalid, await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task A_failure_after_consumption_never_restores_the_credential_or_writes_a_file()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        fixture.Validator.RejectionCode = "database.connection_failed";

        using var rejected = await fixture.PostAsync(credential);
        fixture.Validator.RejectionCode = null;
        using var retry = await fixture.PostAsync(credential);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(InvalidRequest, await ErrorCodeAsync(rejected));
        Assert.False(File.Exists(fixture.BootstrapPath));
        // Consumption is one-way: the same credential cannot be presented again.
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Equal(CredentialInvalid, await ErrorCodeAsync(retry));
        Assert.False(File.Exists(fixture.BootstrapPath));
    }

    [Theory]
    [InlineData("candidate.validation_failed", HttpStatusCode.ServiceUnavailable, Unavailable)]
    [InlineData("candidate.invalid_result", HttpStatusCode.ServiceUnavailable, Unavailable)]
    [InlineData("database.provider_invalid_result", HttpStatusCode.ServiceUnavailable, Unavailable)]
    [InlineData("database.provider_validation_failed", HttpStatusCode.ServiceUnavailable, Unavailable)]
    [InlineData("database.provider_not_registered", HttpStatusCode.BadRequest, InvalidRequest)]
    [InlineData("database.connection_string_invalid", HttpStatusCode.BadRequest, InvalidRequest)]
    [InlineData("database.target_not_found", HttpStatusCode.BadRequest, InvalidRequest)]
    public async Task Validator_failures_map_to_the_fixed_table(
        string errorCode,
        HttpStatusCode status,
        string expected)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        fixture.Validator.RejectionCode = errorCode;

        using var response = await fixture.PostAsync(credential);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(expected, await ErrorCodeAsync(response));
        // The validator's own code never reaches the response.
        Assert.DoesNotContain(errorCode, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("null-result")]
    [InlineData("internal-cancellation")]
    [InlineData("timeout")]
    public async Task Internal_validator_failures_are_one_unavailable_result(string scenario)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        fixture.Validator.ReturnsNull = scenario == "null-result";
        fixture.Validator.Failure = scenario switch
        {
            "throws" => new InvalidOperationException("internal detail must not leak"),
            "internal-cancellation" => new OperationCanceledException(
                "internal detail must not leak",
                new CancellationTokenSource().Token),
            "timeout" => new TimeoutException("internal detail must not leak"),
            _ => null,
        };

        using var response = await fixture.PostAsync(credential);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Unavailable, await ErrorCodeAsync(response));
        Assert.DoesNotContain("internal detail", body, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.BootstrapPath));
    }

    [Fact]
    public async Task An_existing_target_and_a_missing_target_are_the_fixed_conflict()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        // Replacing a file that is not there.
        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        using var missing = await fixture.PutAsync(fixture.Cookie(ManagementPermission.Admin));

        // Creating over a file that already exists. The file is published out of band, so the
        // still-valid credential meets a target the store refuses to overwrite.
        fixture.Snapshot.Current = BootstrapManagementHostFixture.BeforeConfiguration;
        new BootstrapFileStore(
            ServiceId.Parse("catalog"),
            new BootstrapDatabaseProviderRegistry([]),
            fixture.BootstrapPath)
            .Create(new BootstrapConfiguration(
                ServiceId.Parse("catalog"),
                new BootstrapDatabaseConfiguration("PostgreSQL", null, "Host=existing;Password=p"),
                "existing-master-key"));
        var existing = await File.ReadAllBytesAsync(fixture.BootstrapPath, Token);
        using var again = await fixture.PostAsync(credential);

        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(Conflict, await ErrorCodeAsync(missing));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(Conflict, await ErrorCodeAsync(again));
        // The refused create left the existing file exactly as it was.
        Assert.Equal(existing, await File.ReadAllBytesAsync(fixture.BootstrapPath, Token));
    }

    [Fact]
    public async Task Two_posts_with_their_own_credentials_produce_exactly_one_created_file()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var first = await fixture.ProvisionAsync();
        using var start = new Barrier(2);
        fixture.Validator.Before = _ =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        };

        // The store admits exactly one provisioning, so the second caller has no credential at all
        // and both requests race the same one.
        var responses = await Task.WhenAll(
            Task.Run(async () => await fixture.PostAsync(first), Token),
            Task.Run(async () => await fixture.PostAsync(first), Token));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.True(File.Exists(fixture.BootstrapPath));
            var loaded = new BootstrapFileStore(
                ServiceId.Parse("catalog"),
                new BootstrapDatabaseProviderRegistry([]),
                fixture.BootstrapPath).Load();
            Assert.Equal(BootstrapManagementHostFixture.MasterKey, loaded.MasterKey);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Sequential_updates_both_succeed_and_leave_a_complete_file()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        using var created = await fixture.PostAsync(await fixture.ProvisionAsync());
        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        using var first = await fixture.PutAsync(
            cookie,
            BootstrapManagementHostFixture.CreateBody(connectionString: "Host=one;Password=one"));
        using var second = await fixture.PutAsync(
            cookie,
            """{"masterKey":"second-master-key"}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var loaded = new BootstrapFileStore(
            ServiceId.Parse("catalog"),
            new BootstrapDatabaseProviderRegistry([]),
            fixture.BootstrapPath).Load();
        // The second update retained the database it did not send and replaced the master key.
        Assert.Equal("Host=one;Password=one", loaded.Database.ConnectionString);
        Assert.Equal("second-master-key", loaded.MasterKey);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.BootstrapPath)!, "*.tmp"));
    }

    [Fact]
    public async Task The_credential_header_is_denied_before_any_projection_reads_it()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var registry = fixture.Application.Services
            .GetRequiredService<SensitiveHeaderRegistry>();

        Assert.Contains(CredentialHeader, registry.DeniedHeaderNames, StringComparer.OrdinalIgnoreCase);
        // The built-in list is untouched by this group.
        Assert.Contains("Authorization", registry.DeniedHeaderNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cookie", registry.DeniedHeaderNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_response_ever_projects_a_supplied_secret()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        const string Secret = "Host=db;Password=super-secret-value";

        using var created = await fixture.PostAsync(
            credential,
            BootstrapManagementHostFixture.CreateBody(connectionString: Secret));
        var body = await created.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("""{"restartRequired":true}""", body);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, body, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapManagementHostFixture.MasterKey, body, StringComparison.Ordinal);
        foreach (var header in created.Headers)
        {
            Assert.DoesNotContain(Secret, string.Join(",", header.Value), StringComparison.Ordinal);
            Assert.DoesNotContain(credential, string.Join(",", header.Value), StringComparison.Ordinal);
        }

        // The validator did see the exact values, so the projection - not the parser - is what
        // keeps them out of the response.
        Assert.Equal(Secret, fixture.Validator.LastConnectionString);
        Assert.Equal(BootstrapManagementHostFixture.MasterKey, fixture.Validator.LastMasterKey);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Both_entries_require_exactly_one_fixed_request_header(string method)
    {
        var post = method == "POST";
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(
            snapshot: post
                ? BootstrapManagementHostFixture.BeforeConfiguration
                : BootstrapManagementHostFixture.Ready);
        var credential = post ? await fixture.ProvisionAsync() : null;
        var cookie = post ? null : fixture.Cookie(ManagementPermission.Admin);

        foreach (var header in new string?[]?[] { [], [""], ["0"], ["1", "1"] })
        {
            using var response = await fixture.SendAsync(
                post ? HttpMethod.Post : HttpMethod.Put,
                credential: credential is null ? [] : [credential],
                cookie: cookie,
                unsafeHeader: header);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidRequest, await ErrorCodeAsync(response));
        }

        Assert.Equal(0, fixture.Validator.Calls);
        Assert.False(File.Exists(fixture.BootstrapPath));
    }

    [Fact]
    public async Task The_named_rate_limit_bounds_the_creation_entry()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync(setupPermitLimit: 2);
        var credential = await fixture.ProvisionAsync();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await fixture.SendAsync(HttpMethod.Post, credential: []);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.False(File.Exists(fixture.BootstrapPath));
        Assert.NotNull(credential);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
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

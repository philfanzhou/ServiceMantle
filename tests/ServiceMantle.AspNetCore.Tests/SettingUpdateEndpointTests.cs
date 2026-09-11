using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class SettingUpdateEndpointTests
{
    private const string ValidBody =
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"product.name\",\"value\":\"Orders\"}]}";
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "/management/v1/settings")]
    [InlineData("/ops/v1", "/ops/v1/settings")]
    public async Task Update_is_opt_in_under_the_configured_root_and_calls_the_executor_once(
        string? root,
        string expectedPath)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = (_, _, _) => ValueTask.FromResult(ServiceSettingUpdateResult.Applied(1))
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync, root: root);
        using var response = await host.SendAsync(ValidBody, host.AdminCookie());

        Assert.Equal(expectedPath, host.Root + SettingUpdateHostFixture.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"version\":1}", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Baseline_without_opt_in_maps_no_update_endpoint()
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.CreateAsync(executor.ExecuteAsync, mapCount: 0);
        await host.StartAsync();
        using var response = await host.SendAsync(ValidBody, host.AdminCookie());

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("look-alike")]
    [InlineData("nested")]
    [InlineData("missing-executor")]
    public async Task Invalid_mapping_or_missing_executor_fails_before_start(string scenario)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAndStartInvalidAsync(scenario));

        Assert.DoesNotContain(SettingUpdateHostFixture.Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON; CHARSET=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task Accepted_content_types_preserve_values_and_resolve_operator_from_claims(string contentType)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = (_, _, _) => ValueTask.FromResult(ServiceSettingUpdateResult.Applied(1))
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        const string body =
            "{\"expectedVersion\":9223372036854775807,\"changes\":[" +
            "{\"key\":\" Product.Name \",\"value\":\"  Keep\\nExact  \"}," +
            "{\"key\":\"product.optional\",\"value\":null}]}";
        using var response = await host.SendAsync(body, host.AdminCookie(), contentType: contentType);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(long.MaxValue, command.ExpectedVersion);
        Assert.Equal("  Keep\nExact  ", command.Changes["Product.Name"]);
        Assert.Null(command.Changes["product.optional"]);
        Assert.Equal("admin", command.Operator.OperatorId);
        Assert.Equal("interactive_admin", command.Operator.Source.Value);
    }

    [Theory]
    [InlineData("text/plain", null, "")]
    [InlineData("application/x-www-form-urlencoded", null, "")]
    [InlineData("application/json; charset=utf-16", null, "")]
    [InlineData("application/json; profile=management", null, "")]
    [InlineData("application/json", "gzip", "")]
    [InlineData("application/json", null, "?extra=1")]
    public async Task Unsupported_media_encoding_or_query_is_rejected_without_execution(
        string contentType,
        string? contentEncoding,
        string query)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        using var response = await host.SendAsync(
            ValidBody,
            host.AdminCookie(),
            query,
            contentType,
            contentEncoding);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, executor.Calls);
    }

    public static TheoryData<string> InvalidBodies => new()
    {
        "",
        "null",
        "[]",
        "{}",
        "{\"expectedVersion\":0}",
        "{\"changes\":[]}",
        "{\"ExpectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":null}]}",
        "{\"expectedVersion\":0,\"expectedVersion\":1,\"changes\":[{\"key\":\"a\",\"value\":null}]}",
        "{\"expectedVersion\":0,\"changes\":[],\"unknown\":true}",
        "{\"expectedVersion\":-1,\"changes\":[{\"key\":\"a\",\"value\":null}]}",
        "{\"expectedVersion\":1.0,\"changes\":[{\"key\":\"a\",\"value\":null}]}",
        "{\"expectedVersion\":9223372036854775808,\"changes\":[{\"key\":\"a\",\"value\":null}]}",
        "{\"expectedVersion\":0,\"changes\":[]}",
        "{\"expectedVersion\":0,\"changes\":[null]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\"}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"value\":\"x\"}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":1}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":true}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":null,\"extra\":1}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"key\":\"b\",\"value\":null}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"\",\"value\":null}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"   \",\"value\":null}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\" Name \",\"value\":\"a\"},{\"key\":\"name\",\"value\":\"b\"}]}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":\"x\"}],}",
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":\"x\"}]} trailing"
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Strict_json_rejections_are_fixed_and_have_no_partial_execution(string body)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        using var response = await host.SendAsync(body, host.AdminCookie());
        var responseBody = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ManagementApiDefaults.InvalidRequestErrorCode, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingUpdateHostFixture.Secret, responseBody, StringComparison.Ordinal);
        Assert.Equal(0, executor.Calls);
        SettingUpdateHostFixture.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Change_count_key_length_depth_and_body_length_boundaries_are_enforced()
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        var thirtyTwo = Body(Enumerable.Range(0, 32).Select(index => ($"key-{index}", (string?)"value")));
        var thirtyThree = Body(Enumerable.Range(0, 33).Select(index => ($"key-{index}", (string?)"value")));
        var key128 = Body([(new string('k', 128), (string?)"value")]);
        var key129 = Body([(new string('k', 129), (string?)"value")]);
        const string prefix = "{\"expectedVersion\":0,\"changes\":[{\"key\":\"large\",\"value\":\"";
        const string suffix = "\"}]}";
        var exactBody = prefix + new string('x', (256 * 1024) - prefix.Length - suffix.Length) + suffix;
        var oversizedBody = exactBody + " ";
        var deepBody =
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"a\",\"value\":\"x\"}]," +
            "\"extra\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{}}}}}}}}}";

        foreach (var accepted in new[] { thirtyTwo, key128, exactBody })
        {
            using var response = await host.SendAsync(accepted, host.AdminCookie());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var calls = executor.Calls;
        foreach (var rejected in new[] { thirtyThree, key129, oversizedBody, deepBody })
        {
            using var response = await host.SendAsync(rejected, host.AdminCookie());
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(calls, executor.Calls);
    }

    [Theory]
    [InlineData(ServiceSettingUpdateStatus.Applied, 200)]
    [InlineData(ServiceSettingUpdateStatus.ValidationFailed, 400)]
    [InlineData(ServiceSettingUpdateStatus.VersionConflict, 409)]
    [InlineData(ServiceSettingUpdateStatus.VersionExhausted, 409)]
    [InlineData(ServiceSettingUpdateStatus.ProtectionFailed, 503)]
    [InlineData(ServiceSettingUpdateStatus.StorageFailed, 503)]
    [InlineData(ServiceSettingUpdateStatus.TransactionRequired, 503)]
    [InlineData(ServiceSettingUpdateStatus.ContextNotClean, 503)]
    public async Task Closed_update_statuses_have_fixed_HTTP_mappings(ServiceSettingUpdateStatus status, int expected)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = (_, _, _) => ValueTask.FromResult(
                status == ServiceSettingUpdateStatus.Applied
                    ? ServiceSettingUpdateResult.Applied(9)
                    : ServiceSettingUpdateResult.Failure(status))
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        using var response = await host.SendAsync(ValidBody, host.AdminCookie());
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected switch
        {
            200 => "{\"version\":9}",
            503 => "{\"errorCode\":\"management.settings.update_unavailable\"}",
            _ => body
        }, body);
        Assert.DoesNotContain(SettingUpdateHostFixture.Secret, body, StringComparison.Ordinal);
        Assert.Equal(1, executor.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("exception")]
    [InlineData("internal-cancel")]
    public async Task Invalid_results_exceptions_and_internal_cancellation_return_one_safe_503(string scenario)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = (_, _, _) => scenario switch
            {
                "null" => ValueTask.FromResult<ServiceSettingUpdateResult>(null!),
                "exception" => ValueTask.FromException<ServiceSettingUpdateResult>(
                    new InvalidOperationException(SettingUpdateHostFixture.Secret)),
                _ => ValueTask.FromException<ServiceSettingUpdateResult>(
                    new OperationCanceledException(SettingUpdateHostFixture.Secret))
            }
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync, environment: "Development");
        using var response = await host.SendAsync(ValidBody, host.AdminCookie());
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"errorCode\":\"management.settings.update_unavailable\"}", body);
        Assert.DoesNotContain(SettingUpdateHostFixture.Secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_waits_until_the_consumer_executor_releases_its_commit_barrier()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = async (_, _, cancellationToken) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return ServiceSettingUpdateResult.Applied(1);
            }
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        var pending = host.Track(host.SendAsync(ValidBody, host.AdminCookie()));
        await entered.Task.WaitAsync(Observation, Token);

        Assert.False(pending.IsCompleted);
        release.SetResult();
        using var response = await pending.WaitAsync(Observation, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_keep_scope_correlation_and_cancellation_isolated()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new Dictionary<string, (IServiceProvider Services, CancellationToken Token)>();
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = async (context, _, cancellationToken) =>
            {
                var correlationId = context.GetServiceMantleCorrelationId()!;
                lock (observations)
                {
                    observations.Add(correlationId, (context.RequestServices, cancellationToken));
                }

                if (correlationId == "update-first")
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                    return ServiceSettingUpdateResult.Applied(1);
                }

                return ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.VersionConflict);
            }
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        var firstAbort = host.CreateAbort();
        var secondAbort = host.CreateAbort();
        var first = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, firstAbort.Token, "update-first"), Token));
        await firstEntered.Task.WaitAsync(Observation, Token);
        var second = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, secondAbort.Token, "update-second"), Token));

        var secondContext = await second.WaitAsync(Observation, Token);
        Assert.Equal(StatusCodes.Status409Conflict, secondContext.Response.StatusCode);
        Assert.False(first.IsCompleted);
        releaseFirst.SetResult();
        var firstContext = await first.WaitAsync(Observation, Token);

        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.NotSame(observations["update-first"].Services, observations["update-second"].Services);
        Assert.Equal(firstAbort.Token, observations["update-first"].Token);
        Assert.Equal(secondAbort.Token, observations["update-second"].Token);
        Assert.Equal(
            "update-first",
            Assert.Single(firstContext.Response.Headers[ServiceHeaderNames.CorrelationId].ToArray()));
        Assert.Equal(
            "update-second",
            Assert.Single(secondContext.Response.Headers[ServiceHeaderNames.CorrelationId].ToArray()));
    }

    [Fact]
    public async Task Caller_cancellation_is_passed_through_without_a_fixed_error()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new SettingUpdateHostFixture.RecordingExecutor
        {
            Handler = async (_, _, cancellationToken) =>
            {
                entered.SetResult();
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(cancelled.SetResult);
                await cancelled.Task;
                throw new InvalidOperationException(SettingUpdateHostFixture.Secret);
            }
        };
        await using var host = await SettingUpdateHostFixture.StartAsync(executor.ExecuteAsync);
        var abort = host.CreateAbort();
        var pending = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, abort.Token, "cancelled-update"),
            Token));
        await entered.Task.WaitAsync(Observation, Token);
        await abort.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.True(executor.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task An_unresolved_operator_invokes_forbid_without_execution()
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(
            executor.ExecuteAsync,
            resolver: new RejectingResolver());
        using var response = await host.SendAsync(ValidBody, host.AdminCookie());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("gate", 503)]
    [InlineData("none", 401)]
    [InlineData("corrupted", 401)]
    [InlineData("expired", 401)]
    [InlineData("read-only", 403)]
    [InlineData("invalid-claims", 403)]
    public async Task Gate_and_authorization_rejections_never_execute(string session, int status)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(
            executor.ExecuteAsync,
            health: session == "gate"
                ? new ServiceHealthSnapshot(
                    ServiceStartupPhase.PendingSetup,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Reachable)
                : null);
        using var response = await host.SendAsync(ValidBody, Session(host, session));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, executor.Calls);
        SettingUpdateHostFixture.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Rate_limit_rejects_before_the_second_execution()
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        await using var host = await SettingUpdateHostFixture.StartAsync(
            executor.ExecuteAsync,
            managementPermitLimit: 1);
        var cookie = host.AdminCookie();
        using var accepted = await host.SendAsync(ValidBody, cookie);
        using var rejected = await host.SendAsync(ValidBody, cookie);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, executor.Calls);
    }

    private static async Task CreateAndStartInvalidAsync(string scenario)
    {
        var executor = new SettingUpdateHostFixture.RecordingExecutor();
        var fixture = scenario switch
        {
            "duplicate" => await SettingUpdateHostFixture.CreateAsync(executor.ExecuteAsync, mapCount: 2),
            "outside" => await SettingUpdateHostFixture.CreateAsync(
                executor.ExecuteAsync,
                mapCount: 0,
                outside: application => application.MapServiceMantleSettingUpdates(executor.ExecuteAsync)),
            "look-alike" => await SettingUpdateHostFixture.CreateAsync(
                executor.ExecuteAsync,
                mapCount: 0,
                outside: application => application.MapGroup("/other/v1")
                    .MapServiceMantleSettingUpdates(executor.ExecuteAsync)),
            "nested" => await SettingUpdateHostFixture.CreateAsync(
                executor.ExecuteAsync,
                mapCount: 0,
                children: group => group.MapGroup("/nested")
                    .MapServiceMantleSettingUpdates(executor.ExecuteAsync)),
            "missing-executor" => await SettingUpdateHostFixture.CreateAsync(null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await using (fixture)
        {
            await fixture.StartAsync();
        }
    }

    private static string Body(IEnumerable<(string Key, string? Value)> changes) =>
        JsonSerializer.Serialize(new
        {
            expectedVersion = 0,
            changes = changes.Select(change => new { key = change.Key, value = change.Value })
        });

    private static void Configure(
        HttpContext context,
        SettingUpdateHostFixture host,
        CancellationToken abort,
        string correlationId)
    {
        context.Request.Method = "POST";
        context.Request.Path = host.Root + SettingUpdateHostFixture.Path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(ValidBody));
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(ValidBody);
        context.Request.Headers.Cookie = host.AdminCookie();
        context.Request.Headers[ServiceHeaderNames.CorrelationId] = correlationId;
        context.RequestAborted = abort;
    }

    private static string? Session(SettingUpdateHostFixture host, string session) => session switch
    {
        "none" => null,
        "corrupted" => SettingUpdateHostFixture.CorruptedCookie(),
        "expired" => host.Cookie("admin", ManagementPermission.Admin, TimeSpan.FromMinutes(30)),
        "read-only" => host.Cookie("reader", ManagementPermission.Read),
        "invalid-claims" => host.CookieWithInvalidClaims(),
        _ => host.AdminCookie()
    };

    private sealed class RejectingResolver : IManagementCurrentOperatorResolver
    {
        public ManagementCurrentOperatorResult Resolve(System.Security.Claims.ClaimsPrincipal? principal) =>
            ManagementCurrentOperatorResult.ClaimsInvalid(
                WellKnownManagementIdentityErrorCodes.PermissionInvalid);
    }
}

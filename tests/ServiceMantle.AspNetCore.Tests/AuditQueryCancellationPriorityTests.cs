using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.ManagementApi.AuditQueries;
using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the request-cancellation exception priority of the management audit query handler:
/// once <c>RequestAborted</c> has been observed by the time a query dependency (including the
/// scoped service resolution) settles, the caller's cancellation takes precedence over the
/// ordinary 400/503 response mapping and over propagating the dependency's own cancellation.
/// The shared host fixture is reused but not modified; the query doubles are private to this file.
/// </summary>
public sealed class AuditQueryCancellationPriorityTests
{
    private const string FixedCallerCancellationMessage =
        "The management audit query request was cancelled by the caller.";
    private const string QueryErrorSecret = "audit-query-error-canary";
    private const string EntityInvalidSecret = "audit-entity-invalid-canary";
    private const string OrdinarySecret = "Host=private;Password=audit-ordinary-canary";
    private const string InternalCancellationSecret =
        "Host=private;Password=audit-internal-cancellation-canary";
    private const string InternalCancellationInnerSecret = "audit-internal-cancellation-inner-canary";

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<QuerySettlement, bool> SettlementMatrix
    {
        get
        {
            var data = new TheoryData<QuerySettlement, bool>();
            foreach (var settlement in Enum.GetValues<QuerySettlement>())
            {
                data.Add(settlement, true);
                data.Add(settlement, false);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SettlementMatrix))]
    public async Task Cancelled_caller_never_receives_an_ordinary_result_from_the_handler(
        QuerySettlement settlement,
        bool settleSynchronously)
    {
        using var abort = new CancellationTokenSource();
        var service = new CancellingQueryService(abort, settlement, settleSynchronously, cancelCaller: true);
        var provider = new SingleServiceQueryProvider(service);
        var context = CreateHandlerContext(provider, abort.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => AuditQueryHandlers.QueryAsync(context));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(1, service.Calls);
        Assert.Equal(1, provider.ResolveCount);
    }

    [Theory]
    [MemberData(nameof(SettlementMatrix))]
    public async Task Uncancelled_control_keeps_the_closed_response_mapping(
        QuerySettlement settlement,
        bool settleSynchronously)
    {
        var service = new CancellingQueryService(
            abort: null,
            settlement,
            settleSynchronously,
            cancelCaller: false);
        var provider = new SingleServiceQueryProvider(service);
        var context = CreateHandlerContext(provider, CancellationToken.None);

        var result = await AuditQueryHandlers.QueryAsync(context);

        var (statusCode, body) = await SerializeAsync(result);
        Assert.Equal(ExpectedControlStatus(settlement), statusCode);
        Assert.Equal(1, service.Calls);
        AssertFreeOfSecrets(body);
        if (settlement == QuerySettlement.ReturnsPage)
        {
            Assert.Contains("\"items\"", body, StringComparison.Ordinal);
            Assert.Contains("\"hasNextPage\"", body, StringComparison.Ordinal);
        }
        else if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            Assert.Equal("{\"errorCode\":\"management.audit.unavailable\"}", body);
        }
    }

    [Fact]
    public async Task Precancelled_request_never_resolves_the_service_or_queries()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var service = new CancellingQueryService(
            abort,
            QuerySettlement.ReturnsPage,
            settleSynchronously: true,
            cancelCaller: false);
        var provider = new SingleServiceQueryProvider(service);
        var context = CreateHandlerContext(provider, abort.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => AuditQueryHandlers.QueryAsync(context));

        Assert.Equal(abort.Token, exception.CancellationToken);
        Assert.Equal(0, provider.ResolveCount);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Di_query_factory_cancellation_and_failure_answer_the_caller_not_503()
    {
        using var abort = new CancellationTokenSource();
        var service = new AuditQueryHostFixture.RecordingQueryService();
        var factoryCalls = 0;
        await using var host = await AuditQueryHostFixture.StartAsync(
            service,
            queryFactory: _ =>
            {
                factoryCalls++;
                abort.Cancel();
                throw new InvalidOperationException(OrdinarySecret);
            });

        var request = host.Track(host.Application.GetTestServer().SendAsync(
            context => Configure(context, host, abort.Token, "factory-cancelled-audit-request"),
            Token));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(0, service.Calls);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task Cancelled_request_does_not_pollute_a_concurrent_successful_one()
    {
        var cancelledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = async (query, cancellationToken) =>
            {
                if (query.OperatorId == "cancel")
                {
                    cancelledEntered.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // The abort was observed; the dependency now settles with a mapped query
                        // failure that must not be delivered over the caller's cancellation.
                    }

                    throw new ManagementAuditException("audit.query_cursor_invalid", QueryErrorSecret);
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
                "cancelled-audit-priority",
                "?operatorId=cancel"),
            Token));
        await cancelledEntered.Task.WaitAsync(Observation, Token);
        var successful = host.Track(host.SendAsync(
            cookie: host.AdminCookie(),
            query: "?operatorId=ok",
            correlationId: "successful-audit-priority"));
        await abort.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        AssertSafeCallerCancellation(exception, abort.Token);

        using var response = await successful.WaitAsync(Observation, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "successful-audit-priority",
            Assert.Single(response.Headers.GetValues(ServiceHeaderNames.CorrelationId)));
        AuditQueryHostFixture.AssertSecurityHeaders(response);
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task Uncancelled_null_result_keeps_the_fixed_503_on_the_real_pipeline()
    {
        var service = new AuditQueryHostFixture.RecordingQueryService
        {
            Handler = (query, _) => ValueTask.FromResult<ManagementAuditQueryResult>(null!)
        };
        await using var host = await AuditQueryHostFixture.StartAsync(service, environment: "Development");
        using var response = await host.SendAsync(cookie: host.AdminCookie());
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"errorCode\":\"management.audit.unavailable\"}", body);
        Assert.Equal(1, service.Calls);
        AuditQueryHostFixture.AssertSecurityHeaders(response);
    }

    private static int ExpectedControlStatus(QuerySettlement settlement) => settlement switch
    {
        QuerySettlement.ReturnsPage => StatusCodes.Status200OK,
        QuerySettlement.ReturnsNull => StatusCodes.Status503ServiceUnavailable,
        QuerySettlement.ThrowsQueryError => StatusCodes.Status400BadRequest,
        QuerySettlement.ThrowsEntityInvalid => StatusCodes.Status503ServiceUnavailable,
        QuerySettlement.ThrowsOrdinaryException => StatusCodes.Status503ServiceUnavailable,
        QuerySettlement.ThrowsInternalCancellation => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(settlement))
    };

    private static DefaultHttpContext CreateHandlerContext(
        IServiceProvider services,
        CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = requestAborted
        };
        context.Request.Method = "GET";
        context.Request.Path = "/management/v1/audit";
        context.Request.QueryString = QueryString.Empty;
        return context;
    }

    private static async Task<(int StatusCode, string Body)> SerializeAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(stream.ToArray()));
    }

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

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception,
        CancellationToken requestAborted)
    {
        Assert.Equal(FixedCallerCancellationMessage, exception.Message);
        Assert.Equal(requestAborted, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertFreeOfSecrets(exception.Message);
        AssertFreeOfSecrets(exception.ToString());
    }

    private static void AssertFreeOfSecrets(string text)
    {
        Assert.DoesNotContain(QueryErrorSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(EntityInvalidSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(OrdinarySecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationInnerSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuditQueryHostFixture.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    public enum QuerySettlement
    {
        ReturnsPage,
        ReturnsNull,
        ThrowsQueryError,
        ThrowsEntityInvalid,
        ThrowsOrdinaryException,
        ThrowsInternalCancellation
    }

    private sealed class CancellingQueryService(
        CancellationTokenSource? abort,
        QuerySettlement settlement,
        bool settleSynchronously,
        bool cancelCaller) : IManagementAuditQueryService
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        public ValueTask<ManagementAuditQueryResult> QueryAsync(
            ManagementAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            if (cancelCaller)
            {
                abort?.Cancel();
            }

            return settleSynchronously ? SettleCore() : SettleAsynchronouslyAsync();
        }

        private async ValueTask<ManagementAuditQueryResult> SettleAsynchronouslyAsync()
        {
            await Task.Yield();
            return await SettleCore();
        }

        private ValueTask<ManagementAuditQueryResult> SettleCore() => settlement switch
        {
            QuerySettlement.ReturnsPage => ValueTask.FromResult(
                new ManagementAuditQueryResult([], 1, 50, 0)),
            QuerySettlement.ReturnsNull => ValueTask.FromResult<ManagementAuditQueryResult>(null!),
            QuerySettlement.ThrowsQueryError => throw new ManagementAuditException(
                "audit.query_cursor_invalid",
                QueryErrorSecret),
            QuerySettlement.ThrowsEntityInvalid => throw new ManagementAuditException(
                "audit.entity_invalid",
                EntityInvalidSecret),
            QuerySettlement.ThrowsOrdinaryException => throw new InvalidOperationException(
                OrdinarySecret),
            QuerySettlement.ThrowsInternalCancellation => throw new OperationCanceledException(
                InternalCancellationSecret,
                new Exception(InternalCancellationInnerSecret),
                new CancellationToken(true)),
            _ => throw new ArgumentOutOfRangeException(nameof(settlement))
        };
    }

    private sealed class SingleServiceQueryProvider(IManagementAuditQueryService? service) : IServiceProvider
    {
        private int resolveCount;

        internal int ResolveCount => Volatile.Read(ref resolveCount);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IManagementAuditQueryService))
            {
                Interlocked.Increment(ref resolveCount);
                return service;
            }

            return null;
        }
    }
}

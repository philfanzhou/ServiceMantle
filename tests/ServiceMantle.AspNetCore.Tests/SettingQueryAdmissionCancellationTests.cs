using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using ServiceMantle.AspNetCore.ManagementApi.SettingQueries;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the request-cancellation admission and delivery priority of the two management setting
/// query handlers. Once <c>RequestAborted</c> has been observed by the time the bounded query read,
/// the scoped resolution, or the single refresh settles - whether it answered, rejected, failed, or
/// cancelled internally - the caller's cancellation outranks the 400/503/200 response. The handlers
/// are driven directly with a <see cref="DefaultHttpContext"/>, so a phase gate can never intercept
/// the request first; the query-feature, snapshot-source, and service-provider doubles are private
/// to this file and the shared host fixture is not used or modified.
/// </summary>
public sealed class SettingQueryAdmissionCancellationTests
{
    private const string FixedCallerCancellationMessage =
        "The management setting query request was cancelled by the caller.";
    private const string RootKey = "root-key-with-enough-entropy-for-query-admission-tests";
    private const string SensitivePlaintext = "query-admission-sensitive-plaintext-canary";
    private const string OrdinarySecret = "Host=private;Password=query-ordinary-canary";
    private const string InternalCancellationSecret =
        "Host=private;Password=query-internal-cancellation-canary";
    private const string InternalCancellationInnerSecret = "query-internal-cancellation-inner-canary";

    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public enum Route
    {
        Definitions,
        CurrentValues,
    }

    public enum QueryShape
    {
        Legal,
        Illegal,
    }

    public enum QuerySettlement
    {
        ReturnsQuery,
        ThrowsOrdinaryException,
        ThrowsInternalCancellation,
    }

    public enum SourceSettlement
    {
        ReturnsRead,
        ThrowsOrdinaryException,
        ThrowsInternalCancellation,
    }

    public static TheoryData<Route, QueryShape> RoutesAndShapes => new()
    {
        { Route.Definitions, QueryShape.Legal },
        { Route.Definitions, QueryShape.Illegal },
        { Route.CurrentValues, QueryShape.Legal },
        { Route.CurrentValues, QueryShape.Illegal },
    };

    public static TheoryData<Route, QueryShape, QuerySettlement> QueryCancellationMatrix => new()
    {
        { Route.Definitions, QueryShape.Legal, QuerySettlement.ReturnsQuery },
        { Route.Definitions, QueryShape.Illegal, QuerySettlement.ReturnsQuery },
        { Route.Definitions, QueryShape.Legal, QuerySettlement.ThrowsOrdinaryException },
        { Route.Definitions, QueryShape.Legal, QuerySettlement.ThrowsInternalCancellation },
        { Route.CurrentValues, QueryShape.Legal, QuerySettlement.ReturnsQuery },
        { Route.CurrentValues, QueryShape.Illegal, QuerySettlement.ReturnsQuery },
        { Route.CurrentValues, QueryShape.Legal, QuerySettlement.ThrowsOrdinaryException },
        { Route.CurrentValues, QueryShape.Legal, QuerySettlement.ThrowsInternalCancellation },
    };

    [Theory]
    [MemberData(nameof(RoutesAndShapes))]
    public async Task Precancelled_request_never_reads_query_resolves_or_refreshes(
        Route route, QueryShape shape)
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var queryFeature = new CancellingQueryFeature(Query(shape), cancelCaller: null, throwOnGet: null);
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, abort.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await Invoke(route, context));

        Assert.Equal(abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, queryFeature.Reads);
        Assert.Equal(0, provider.ResolveCount);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [MemberData(nameof(QueryCancellationMatrix))]
    public async Task Query_read_cancellation_delivers_caller_token_before_resolve_and_refresh(
        Route route, QueryShape shape, QuerySettlement settlement)
    {
        using var abort = new CancellationTokenSource();
        var queryFeature = new CancellingQueryFeature(
            Query(shape),
            abort,
            settlement switch
            {
                QuerySettlement.ThrowsOrdinaryException => new InvalidOperationException(OrdinarySecret),
                QuerySettlement.ThrowsInternalCancellation => new OperationCanceledException(
                    InternalCancellationSecret,
                    new Exception(InternalCancellationInnerSecret),
                    new CancellationToken(true)),
                _ => null,
            });
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, abort.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await Invoke(route, context));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.True(queryFeature.Reads >= 1);
        Assert.Equal(0, provider.ResolveCount);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(SourceSettlement.ReturnsRead)]
    [InlineData(SourceSettlement.ThrowsOrdinaryException)]
    [InlineData(SourceSettlement.ThrowsInternalCancellation)]
    public async Task Source_settling_with_cancellation_delivers_caller_token(SourceSettlement settlement)
    {
        using var abort = new CancellationTokenSource();
        var queryFeature = new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null);
        var source = new ScriptedSource(settlement, abort, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, abort.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await SettingQueryHandlers.CurrentValuesAsync(context));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(1, provider.ResolveCount);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Lock_wait_cancellation_delivers_caller_token()
    {
        using var holderAbort = new CancellationTokenSource();
        using var waiterAbort = new CancellationTokenSource();
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderSource = new BlockingSource(holderEntered, releaseHolder, ValidRead());
        using var stack = new QueryStack(holderSource);

        // The holder occupies the loader's single-refresh lock and blocks inside the source.
        var holder = SettingQueryHandlers.CurrentValuesAsync(CreateContext(
            new SingleQueryServiceProvider(stack.QueryService),
            new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null),
            holderAbort.Token));
        await holderEntered.Task.WaitAsync(Observation, Token);

        // The waiter runs synchronously up to the lock wait (its token is not yet cancelled), so it
        // is parked on the held lock before the cancellation below.
        var waiter = SettingQueryHandlers.CurrentValuesAsync(CreateContext(
            new SingleQueryServiceProvider(stack.QueryService),
            new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null),
            waiterAbort.Token));
        await waiterAbort.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => waiter.WaitAsync(Observation, Token));
        releaseHolder.SetResult();
        var (holderStatus, _) = await SerializeAsync(await holder.WaitAsync(Observation, Token));

        AssertSafeCallerCancellation(exception, waiterAbort.Token);
        Assert.Equal(StatusCodes.Status200OK, holderStatus);
        Assert.Equal(1, holderSource.Calls);
        Assert.False(holderAbort.IsCancellationRequested);
    }

    [Theory]
    [InlineData(Route.Definitions, QueryShape.Legal, StatusCodes.Status200OK)]
    [InlineData(Route.Definitions, QueryShape.Illegal, StatusCodes.Status400BadRequest)]
    [InlineData(Route.CurrentValues, QueryShape.Legal, StatusCodes.Status200OK)]
    [InlineData(Route.CurrentValues, QueryShape.Illegal, StatusCodes.Status400BadRequest)]
    public async Task Uncancelled_admission_keeps_the_existing_mapping(
        Route route, QueryShape shape, int expected)
    {
        var queryFeature = new CancellingQueryFeature(Query(shape), cancelCaller: null, throwOnGet: null);
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, CancellationToken.None);

        var result = await Invoke(route, context);
        var (statusCode, _) = await SerializeAsync(result);

        Assert.Equal(expected, statusCode);
        if (shape == QueryShape.Illegal)
        {
            Assert.Equal(0, provider.ResolveCount);
            Assert.Equal(0, source.Calls);
        }
    }

    [Fact]
    public async Task Uncancelled_empty_match_returns_200_with_an_empty_projection()
    {
        var queryFeature = new CancellingQueryFeature(
            new QueryCollection(new Dictionary<string, StringValues> { ["group"] = "billing" }),
            cancelCaller: null,
            throwOnGet: null);
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);

        var definitions = await SettingQueryHandlers.Definitions(
            CreateContext(provider, queryFeature, CancellationToken.None));
        var current = await SettingQueryHandlers.CurrentValuesAsync(
            CreateContext(provider, queryFeature, CancellationToken.None));
        var (definitionsStatus, definitionsBody) = await SerializeAsync(definitions);
        var (currentStatus, currentBody) = await SerializeAsync(current);

        Assert.Equal(StatusCodes.Status200OK, definitionsStatus);
        Assert.Equal(StatusCodes.Status200OK, currentStatus);
        Assert.Contains("\"definitions\":[]", definitionsBody, StringComparison.Ordinal);
        Assert.Contains("\"values\":[]", currentBody, StringComparison.Ordinal);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Uncancelled_refresh_failure_returns_the_fixed_503()
    {
        var queryFeature = new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null);
        // Version 1 with no values fails the required product.name, so the complete refresh fails.
        var source = new ScriptedSource(
            SourceSettlement.ReturnsRead, cancelCaller: null, SettingQueryHostFixture.Read(1));
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, CancellationToken.None);

        var result = await SettingQueryHandlers.CurrentValuesAsync(context);
        var (statusCode, body) = await SerializeAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Equal("{\"errorCode\":\"management.settings.unavailable\"}", body);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Uncancelled_success_projects_a_sensitive_value_as_null_without_echoing_it()
    {
        var queryFeature = new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null);
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, CancellationToken.None);

        var result = await SettingQueryHandlers.CurrentValuesAsync(context);
        var (statusCode, body) = await SerializeAsync(result);

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Contains("\"version\":7", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"Orders\"", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":null", body, StringComparison.Ordinal);
        AssertFreeOfSecrets(body);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Uncancelled_unmapped_query_exception_is_not_converted_to_503()
    {
        var queryFeature = new CancellingQueryFeature(
            Query(QueryShape.Legal), cancelCaller: null, throwOnGet: new InvalidOperationException(OrdinarySecret));
        var source = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var stack = new QueryStack(source);
        var provider = new SingleQueryServiceProvider(stack.QueryService);
        var context = CreateContext(provider, queryFeature, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await SettingQueryHandlers.CurrentValuesAsync(context));

        Assert.Equal(OrdinarySecret, exception.Message);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_keep_cancellation_and_counts_isolated()
    {
        using var cancelledAbort = new CancellationTokenSource();
        var cancelledSource = new ScriptedSource(SourceSettlement.ReturnsRead, cancelledAbort, ValidRead());
        using var cancelledStack = new QueryStack(cancelledSource);
        var cancelledContext = CreateContext(
            new SingleQueryServiceProvider(cancelledStack.QueryService),
            new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null),
            cancelledAbort.Token);

        using var liveAbort = new CancellationTokenSource();
        var liveSource = new ScriptedSource(SourceSettlement.ReturnsRead, cancelCaller: null, ValidRead());
        using var liveStack = new QueryStack(liveSource);
        var liveContext = CreateContext(
            new SingleQueryServiceProvider(liveStack.QueryService),
            new CancellingQueryFeature(Query(QueryShape.Legal), cancelCaller: null, throwOnGet: null),
            liveAbort.Token);

        var cancelledTask = SettingQueryHandlers.CurrentValuesAsync(cancelledContext);
        var liveTask = SettingQueryHandlers.CurrentValuesAsync(liveContext);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancelledTask.WaitAsync(Observation, Token));
        var (statusCode, body) = await SerializeAsync(await liveTask.WaitAsync(Observation, Token));

        AssertSafeCallerCancellation(exception, cancelledAbort.Token);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Contains("\"version\":7", body, StringComparison.Ordinal);
        Assert.Equal(1, cancelledSource.Calls);
        Assert.Equal(1, liveSource.Calls);
        Assert.True(cancelledAbort.IsCancellationRequested);
        Assert.False(liveAbort.IsCancellationRequested);
    }

    private static Task<IResult> Invoke(Route route, HttpContext context) =>
        route == Route.Definitions
            ? SettingQueryHandlers.Definitions(context)
            : SettingQueryHandlers.CurrentValuesAsync(context);

    private static ServiceSettingSnapshotRead ValidRead() => SettingQueryHostFixture.Read(
        7,
        SettingQueryHostFixture.Value("product.name", 7, ServiceSettingValueType.String, "Orders"),
        SettingQueryHostFixture.Value(
            "product.token",
            7,
            ServiceSettingValueType.String,
            SettingQueryHostFixture.Protect("product.token", SensitivePlaintext, RootKey)));

    private static IQueryCollection Query(QueryShape shape) => shape == QueryShape.Legal
        ? new QueryCollection(new Dictionary<string, StringValues>())
        : new QueryCollection(new Dictionary<string, StringValues> { ["unknown"] = "x" });

    private static DefaultHttpContext CreateContext(
        IServiceProvider services, IQueryFeature queryFeature, CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = requestAborted,
        };
        context.Features.Set(queryFeature);
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

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception, CancellationToken requestAborted)
    {
        Assert.Equal(FixedCallerCancellationMessage, exception.Message);
        Assert.Equal(requestAborted, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertFreeOfSecrets(exception.Message);
        AssertFreeOfSecrets(exception.ToString());
    }

    private static void AssertFreeOfSecrets(string text)
    {
        Assert.DoesNotContain(SensitivePlaintext, text, StringComparison.Ordinal);
        Assert.DoesNotContain(OrdinarySecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationInnerSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class QueryStack : IDisposable
    {
        private readonly ServiceSettingSnapshotLoader loader;

        public QueryStack(IServiceSettingSnapshotSource source)
        {
            var registry = new ServiceSettingDefinitionRegistry([new DefinitionProvider(Catalog())]);
            var accessor = new ServiceSettingCurrentSnapshotAccessor();
            loader = new ServiceSettingSnapshotLoader(
                Service, source, registry, accessor, new RootKeySource());
            QueryService = new ServiceSettingQueryService(registry, loader);
        }

        public ServiceSettingQueryService QueryService { get; }

        private static ServiceSettingDefinition[] Catalog() =>
        [
            new("product.name", ServiceSettingValueType.String, isRequired: true),
            new("product.token", ServiceSettingValueType.String, isSensitive: true),
        ];

        public void Dispose() => loader.Dispose();
    }

    private sealed class DefinitionProvider(IEnumerable<ServiceSettingDefinition> definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }

    private sealed class RootKeySource : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RootKey);
    }

    private sealed class SingleQueryServiceProvider(ServiceSettingQueryService service) : IServiceProvider
    {
        private int resolveCount;

        public int ResolveCount => Volatile.Read(ref resolveCount);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ServiceSettingQueryService))
            {
                Interlocked.Increment(ref resolveCount);
                return service;
            }

            return null;
        }
    }

    private sealed class CancellingQueryFeature(
        IQueryCollection query,
        CancellationTokenSource? cancelCaller,
        Exception? throwOnGet) : IQueryFeature
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public IQueryCollection Query
        {
            get
            {
                Interlocked.Increment(ref reads);
                cancelCaller?.Cancel();
                if (throwOnGet is not null)
                {
                    throw throwOnGet;
                }

                return query;
            }
            set { }
        }
    }

    private sealed class ScriptedSource(
        SourceSettlement settlement,
        CancellationTokenSource? cancelCaller,
        ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            cancelCaller?.Cancel();
            return settlement switch
            {
                SourceSettlement.ReturnsRead => ValueTask.FromResult(read),
                SourceSettlement.ThrowsOrdinaryException =>
                    ValueTask.FromException<ServiceSettingSnapshotRead>(
                        new InvalidOperationException(OrdinarySecret)),
                SourceSettlement.ThrowsInternalCancellation =>
                    ValueTask.FromException<ServiceSettingSnapshotRead>(
                        new OperationCanceledException(
                            InternalCancellationSecret,
                            new Exception(InternalCancellationInnerSecret),
                            new CancellationToken(true))),
                _ => throw new ArgumentOutOfRangeException(nameof(settlement)),
            };
        }
    }

    private sealed class BlockingSource(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public async ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return read;
        }
    }
}

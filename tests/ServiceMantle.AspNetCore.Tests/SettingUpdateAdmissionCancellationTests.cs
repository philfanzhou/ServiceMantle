using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers the request-cancellation admission priority of the management setting update handler:
/// once <c>RequestAborted</c> has been observed by the time the operator resolver or the body parser
/// settles - whether it answered, rejected, returned null, or cancelled internally - the caller's
/// cancellation takes precedence over the Forbid/400 classification and over the next dependency
/// call. The shared host fixture is not used; the resolver, body-stream, and executor doubles are
/// private to this file and drive the handler directly.
/// </summary>
public sealed class SettingUpdateAdmissionCancellationTests
{
    private const string ValidBody =
        "{\"expectedVersion\":0,\"changes\":[{\"key\":\"product.name\",\"value\":\"Orders\"}]}";
    private const string OrdinarySecret = "Host=private;Password=update-ordinary-canary";
    private const string InternalCancellationSecret =
        "Host=private;Password=update-internal-cancellation-canary";
    private const string InternalCancellationInnerSecret = "update-internal-cancellation-inner-canary";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Precancelled_request_never_resolves_reads_body_or_executes()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var resolver = new ScriptedResolver(ResolverSettlement.ReturnsResolved, cancelCaller: null);
        var provider = new SingleResolverProvider(resolver);
        var body = new ScriptedBodyStream(BodySettlement.ReturnsValidJson, cancelCaller: null);
        var executor = new RecordingExecutor();
        var context = CreateContext(provider, body, abort.Token, ValidBody.Length);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => SettingUpdateHandlers.UpdateAsync(context, executor.ExecuteAsync));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(0, provider.ResolveCount);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, body.ReadCount);
        Assert.Equal(0, executor.Calls);
    }

    public static TheoryData<ResolverSettlement> ResolverSettlements => new()
    {
        ResolverSettlement.ReturnsResolved,
        ResolverSettlement.ReturnsUnauthenticated,
        ResolverSettlement.ReturnsClaimsInvalid,
        ResolverSettlement.ReturnsNull,
        ResolverSettlement.ThrowsOrdinaryException,
        ResolverSettlement.ThrowsInternalCancellation,
    };

    [Theory]
    [MemberData(nameof(ResolverSettlements))]
    public async Task Resolver_cancellation_delivers_caller_token_before_body_and_executor(
        ResolverSettlement settlement)
    {
        using var abort = new CancellationTokenSource();
        var resolver = new ScriptedResolver(settlement, abort);
        var provider = new SingleResolverProvider(resolver);
        var body = new ScriptedBodyStream(BodySettlement.ReturnsValidJson, cancelCaller: null);
        var executor = new RecordingExecutor();
        var context = CreateContext(provider, body, abort.Token, ValidBody.Length);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => SettingUpdateHandlers.UpdateAsync(context, executor.ExecuteAsync));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, body.ReadCount);
        Assert.Equal(0, executor.Calls);
    }

    public static TheoryData<BodySettlement> BodySettlements => new()
    {
        BodySettlement.ReturnsValidJson,
        BodySettlement.ReturnsEmptyBody,
        BodySettlement.ReturnsMalformedJson,
        BodySettlement.ReturnsOversizedRead,
        BodySettlement.ThrowsOrdinaryException,
        BodySettlement.ThrowsInternalCancellation,
    };

    [Theory]
    [MemberData(nameof(BodySettlements))]
    public async Task Body_read_cancellation_delivers_caller_token_before_executor(
        BodySettlement settlement)
    {
        using var abort = new CancellationTokenSource();
        var resolver = new ScriptedResolver(ResolverSettlement.ReturnsResolved, cancelCaller: null);
        var provider = new SingleResolverProvider(resolver);
        var body = new ScriptedBodyStream(settlement, abort);
        var executor = new RecordingExecutor();
        var context = CreateContext(
            provider,
            body,
            abort.Token,
            settlement == BodySettlement.ReturnsOversizedRead ? null : ValidBody.Length);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => SettingUpdateHandlers.UpdateAsync(context, executor.ExecuteAsync));

        AssertSafeCallerCancellation(exception, abort.Token);
        Assert.Equal(1, resolver.Calls);
        Assert.True(body.ReadCount >= 1);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Uncancelled_resolved_valid_body_applies_with_one_executor_call()
    {
        var (result, executor, resolver, body) = await RunUncancelledAsync(
            ResolverSettlement.ReturnsResolved,
            BodySettlement.ReturnsValidJson,
            executorResult: ServiceSettingUpdateResult.Applied(7));

        var (statusCode, responseBody) = await SerializeAsync(result);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal("{\"version\":7}", responseBody);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, executor.Calls);
        Assert.True(body.ReadCount >= 1);
    }

    [Theory]
    [MemberData(nameof(RejectingResolverSettlements))]
    public async Task Uncancelled_rejected_resolver_forbids_without_body_or_executor(
        ResolverSettlement settlement)
    {
        var (result, executor, resolver, body) = await RunUncancelledAsync(
            settlement,
            BodySettlement.ReturnsValidJson,
            executorResult: ServiceSettingUpdateResult.Applied(1));

        Assert.IsType<ForbidHttpResult>(result);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, body.ReadCount);
        Assert.Equal(0, executor.Calls);
    }

    public static TheoryData<ResolverSettlement> RejectingResolverSettlements => new()
    {
        ResolverSettlement.ReturnsUnauthenticated,
        ResolverSettlement.ReturnsClaimsInvalid,
        ResolverSettlement.ReturnsNull,
    };

    [Theory]
    [InlineData(BodySettlement.ReturnsEmptyBody, StatusCodes.Status400BadRequest)]
    [InlineData(BodySettlement.ReturnsMalformedJson, StatusCodes.Status400BadRequest)]
    [InlineData(BodySettlement.ReturnsOversizedRead, StatusCodes.Status400BadRequest)]
    public async Task Uncancelled_invalid_body_returns_400_without_executor(
        BodySettlement settlement, int expected)
    {
        var (result, executor, _, _) = await RunUncancelledAsync(
            ResolverSettlement.ReturnsResolved,
            settlement,
            executorResult: ServiceSettingUpdateResult.Applied(1));

        var (statusCode, _) = await SerializeAsync(result);
        Assert.Equal(expected, statusCode);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData(ServiceSettingUpdateStatus.ValidationFailed, StatusCodes.Status400BadRequest)]
    [InlineData(ServiceSettingUpdateStatus.VersionConflict, StatusCodes.Status409Conflict)]
    [InlineData(ServiceSettingUpdateStatus.VersionExhausted, StatusCodes.Status409Conflict)]
    [InlineData(ServiceSettingUpdateStatus.StorageFailed, StatusCodes.Status503ServiceUnavailable)]
    public async Task Uncancelled_executor_statuses_keep_fixed_mappings(
        ServiceSettingUpdateStatus status, int expected)
    {
        var (result, executor, _, _) = await RunUncancelledAsync(
            ResolverSettlement.ReturnsResolved,
            BodySettlement.ReturnsValidJson,
            executorResult: ServiceSettingUpdateResult.Failure(status));

        var (statusCode, _) = await SerializeAsync(result);
        Assert.Equal(expected, statusCode);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task Uncancelled_internal_executor_cancellation_is_a_safe_503()
    {
        using var abort = new CancellationTokenSource();
        var resolver = new ScriptedResolver(ResolverSettlement.ReturnsResolved, cancelCaller: null);
        var provider = new SingleResolverProvider(resolver);
        var body = new ScriptedBodyStream(BodySettlement.ReturnsValidJson, cancelCaller: null);
        var executor = new RecordingExecutor
        {
            Handler = _ => ValueTask.FromException<ServiceSettingUpdateResult>(
                new OperationCanceledException(
                    InternalCancellationSecret,
                    new Exception(InternalCancellationInnerSecret),
                    new CancellationToken(true))),
        };
        var context = CreateContext(provider, body, abort.Token, ValidBody.Length);

        var result = await SettingUpdateHandlers.UpdateAsync(context, executor.ExecuteAsync);
        var (statusCode, responseBody) = await SerializeAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Equal(1, executor.Calls);
        Assert.False(abort.IsCancellationRequested);
        AssertFreeOfSecrets(responseBody);
    }

    [Fact]
    public async Task Concurrent_requests_keep_cancellation_and_counts_isolated()
    {
        using var cancelledAbort = new CancellationTokenSource();
        using var liveAbort = new CancellationTokenSource();
        var cancelledResolver = new ScriptedResolver(ResolverSettlement.ReturnsResolved, cancelledAbort);
        var cancelledExecutor = new RecordingExecutor();
        var cancelledContext = CreateContext(
            new SingleResolverProvider(cancelledResolver),
            new ScriptedBodyStream(BodySettlement.ReturnsValidJson, cancelCaller: null),
            cancelledAbort.Token,
            ValidBody.Length);
        var liveResolver = new ScriptedResolver(ResolverSettlement.ReturnsResolved, cancelCaller: null);
        var liveExecutor = new RecordingExecutor
        {
            Handler = _ => ValueTask.FromResult(ServiceSettingUpdateResult.Applied(3)),
        };
        var liveContext = CreateContext(
            new SingleResolverProvider(liveResolver),
            new ScriptedBodyStream(BodySettlement.ReturnsValidJson, cancelCaller: null),
            liveAbort.Token,
            ValidBody.Length);

        var cancelledTask = SettingUpdateHandlers.UpdateAsync(
            cancelledContext, cancelledExecutor.ExecuteAsync);
        var liveTask = SettingUpdateHandlers.UpdateAsync(liveContext, liveExecutor.ExecuteAsync);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledTask);
        var liveResult = await liveTask;
        var (statusCode, _) = await SerializeAsync(liveResult);

        AssertSafeCallerCancellation(exception, cancelledAbort.Token);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal(0, cancelledExecutor.Calls);
        Assert.Equal(1, liveExecutor.Calls);
        Assert.Equal(liveAbort.Token, liveExecutor.ObservedToken);
        Assert.True(cancelledAbort.IsCancellationRequested);
        Assert.False(liveAbort.IsCancellationRequested);
    }

    private static async Task<(IResult Result, RecordingExecutor Executor, ScriptedResolver Resolver, ScriptedBodyStream Body)>
        RunUncancelledAsync(
            ResolverSettlement resolverSettlement,
            BodySettlement bodySettlement,
            ServiceSettingUpdateResult executorResult)
    {
        var resolver = new ScriptedResolver(resolverSettlement, cancelCaller: null);
        var provider = new SingleResolverProvider(resolver);
        var body = new ScriptedBodyStream(bodySettlement, cancelCaller: null);
        var executor = new RecordingExecutor
        {
            Handler = _ => ValueTask.FromResult(executorResult),
        };
        var context = CreateContext(
            provider,
            body,
            CancellationToken.None,
            bodySettlement == BodySettlement.ReturnsOversizedRead ? null : ValidBody.Length);

        var result = await SettingUpdateHandlers.UpdateAsync(context, executor.ExecuteAsync);
        return (result, executor, resolver, body);
    }

    private static DefaultHttpContext CreateContext(
        IServiceProvider services,
        Stream body,
        CancellationToken requestAborted,
        long? contentLength)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = requestAborted,
        };
        context.Request.Method = "POST";
        context.Request.Path = "/management/v1/settings";
        context.Request.QueryString = QueryString.Empty;
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        context.Request.ContentLength = contentLength;
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
        Assert.Equal(requestAborted, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertFreeOfSecrets(exception.Message);
        AssertFreeOfSecrets(exception.ToString());
    }

    private static void AssertFreeOfSecrets(string text)
    {
        Assert.DoesNotContain(OrdinarySecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InternalCancellationInnerSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    public enum ResolverSettlement
    {
        ReturnsResolved,
        ReturnsUnauthenticated,
        ReturnsClaimsInvalid,
        ReturnsNull,
        ThrowsOrdinaryException,
        ThrowsInternalCancellation,
    }

    public enum BodySettlement
    {
        ReturnsValidJson,
        ReturnsEmptyBody,
        ReturnsMalformedJson,
        ReturnsOversizedRead,
        ThrowsOrdinaryException,
        ThrowsInternalCancellation,
    }

    private sealed class RecordingExecutor
    {
        private int calls;

        public Func<CancellationToken, ValueTask<ServiceSettingUpdateResult>>? Handler { get; set; }
        public int Calls => Volatile.Read(ref calls);
        public CancellationToken ObservedToken { get; private set; }

        public ValueTask<ServiceSettingUpdateResult> ExecuteAsync(
            HttpContext context, ServiceSettingUpdateCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            ObservedToken = cancellationToken;
            return Handler is null
                ? ValueTask.FromResult(ServiceSettingUpdateResult.Applied(1))
                : Handler(cancellationToken);
        }
    }

    private sealed class ScriptedResolver(
        ResolverSettlement settlement,
        CancellationTokenSource? cancelCaller) : IManagementCurrentOperatorResolver
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public ManagementCurrentOperatorResult Resolve(ClaimsPrincipal? principal)
        {
            Interlocked.Increment(ref calls);
            cancelCaller?.Cancel();
            return settlement switch
            {
                ResolverSettlement.ReturnsResolved =>
                    ManagementCurrentOperatorResult.Resolved(Identity()),
                ResolverSettlement.ReturnsUnauthenticated =>
                    ManagementCurrentOperatorResult.Unauthenticated(),
                ResolverSettlement.ReturnsClaimsInvalid =>
                    ManagementCurrentOperatorResult.ClaimsInvalid(
                        WellKnownManagementIdentityErrorCodes.PermissionInvalid),
                ResolverSettlement.ReturnsNull => null!,
                ResolverSettlement.ThrowsOrdinaryException =>
                    throw new InvalidOperationException(OrdinarySecret),
                ResolverSettlement.ThrowsInternalCancellation =>
                    throw new OperationCanceledException(
                        InternalCancellationSecret,
                        new Exception(InternalCancellationInnerSecret),
                        new CancellationToken(true)),
                _ => throw new ArgumentOutOfRangeException(nameof(settlement)),
            };
        }

        private static ManagementIdentity Identity() => ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin",
            [ManagementPermission.Admin]);
    }

    private sealed class SingleResolverProvider(IManagementCurrentOperatorResolver? resolver) : IServiceProvider
    {
        private int resolveCount;

        public int ResolveCount => Volatile.Read(ref resolveCount);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IManagementCurrentOperatorResolver))
            {
                Interlocked.Increment(ref resolveCount);
                return resolver;
            }

            return null;
        }
    }

    private sealed class ScriptedBodyStream(
        BodySettlement settlement,
        CancellationTokenSource? cancelCaller) : Stream
    {
        private const int OversizedTotal = (256 * 1024) + 4096;
        private static readonly byte[] ValidJson = Encoding.UTF8.GetBytes(ValidBody);
        private static readonly byte[] MalformedJson = Encoding.UTF8.GetBytes("{not-json");

        private bool first = true;
        private int position;
        private int emitted;
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readCount);
            if (first)
            {
                first = false;
                cancelCaller?.Cancel();
                switch (settlement)
                {
                    case BodySettlement.ThrowsOrdinaryException:
                        throw new InvalidOperationException(OrdinarySecret);
                    case BodySettlement.ThrowsInternalCancellation:
                        throw new OperationCanceledException(
                            InternalCancellationSecret,
                            new Exception(InternalCancellationInnerSecret),
                            new CancellationToken(true));
                }
            }

            var written = settlement switch
            {
                BodySettlement.ReturnsValidJson => Copy(ValidJson, buffer),
                BodySettlement.ReturnsMalformedJson => Copy(MalformedJson, buffer),
                BodySettlement.ReturnsOversizedRead => EmitOversized(buffer),
                _ => 0,
            };
            return ValueTask.FromResult(written);
        }

        private int Copy(byte[] source, Memory<byte> buffer)
        {
            if (position >= source.Length)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, source.Length - position);
            source.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        private int EmitOversized(Memory<byte> buffer)
        {
            if (emitted >= OversizedTotal)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, OversizedTotal - emitted);
            buffer.Span[..count].Fill((byte)'x');
            emitted += count;
            return count;
        }
    }
}

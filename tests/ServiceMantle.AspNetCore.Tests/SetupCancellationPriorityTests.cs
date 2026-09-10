using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Setup;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers which answer wins when a Setup request is aborted at the same moment as an internal
/// failure: the caller's own cancellation, carrying the caller's own token, and never a fixed
/// result.
/// </summary>
/// <remarks>
/// The handlers are called directly, because an aborted <c>HttpClient</c> throws on the client side
/// whatever the server decided and therefore cannot show which result the server produced.
/// </remarks>
public sealed class SetupCancellationPriorityTests
{
    /// <summary>A syntactically valid Setup Code that must never reach an exception.</summary>
    private const string SentinelCode = "SentinelSetupCode0123456789_-ABC";

    private const string InjectedSecret = "connection-secret-must-not-leak";

    private static readonly ServiceId Service = ServiceId.Parse("catalog");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> StoreOutcomes =>
        ["pending", "completed", "absent", "failure", "foreign-cancellation"];

    public static TheoryData<string> BodyOutcomes =>
        ["valid", "unusable", "failure", "foreign-cancellation"];

    public static TheoryData<string> ExecutorOutcomes =>
    [
        "committed",
        "credential-invalid",
        "conflict",
        "validation-failed",
        "unavailable",
        "null",
        "failure",
        "timeout",
        "foreign-cancellation",
    ];

    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public async Task The_store_boundary_propagates_the_callers_cancellation(string outcome)
    {
        using var abort = new CancellationTokenSource();
        var executor = new RecordingExecutor();
        var context = CreateContext(
            new CancellingStore(outcome, abort),
            abort.Token,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        var completion = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.CompleteAsync(context, executor.Execute));

        AssertCallersCancellation(completion, abort.Token);
        // The phases after the store never run.
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public async Task The_read_entry_store_boundary_propagates_the_callers_cancellation(string outcome)
    {
        using var abort = new CancellationTokenSource();
        var context = CreateContext(new CancellingStore(outcome, abort), abort.Token, body: null);

        var status = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.StatusAsync(context));

        AssertCallersCancellation(status, abort.Token);
    }

    [Theory]
    [MemberData(nameof(BodyOutcomes))]
    public async Task The_body_boundary_propagates_the_callers_cancellation(string outcome)
    {
        using var abort = new CancellationTokenSource();
        var executor = new RecordingExecutor();
        var context = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            abort.Token,
            Body("{}"));
        context.Request.Body = new CancellingBody(outcome, abort);

        var completion = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.CompleteAsync(context, executor.Execute));

        AssertCallersCancellation(completion, abort.Token);
        // A body that was aborted never reaches the consumer transaction.
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [MemberData(nameof(ExecutorOutcomes))]
    public async Task The_executor_boundary_propagates_the_callers_cancellation(string outcome)
    {
        using var abort = new CancellationTokenSource();
        var executor = new RecordingExecutor(outcome, abort);
        var context = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            abort.Token,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        var completion = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.CompleteAsync(context, executor.Execute));

        AssertCallersCancellation(completion, abort.Token);
        // A cancellation observed after the executor returned or threw never calls it again and
        // never retries, rolls back, or commits anything of its own.
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task A_request_cancelled_before_the_handler_never_reaches_the_store()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var store = new FixedStore(InstallationStatus.PendingSetup);
        var executor = new RecordingExecutor();
        var context = CreateContext(store, abort.Token, Body($$"""{"code":"{{SentinelCode}}"}"""));

        var completion = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.CompleteAsync(context, executor.Execute));
        var status = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.StatusAsync(CreateContext(store, abort.Token, null)));

        AssertCallersCancellation(completion, abort.Token);
        AssertCallersCancellation(status, abort.Token);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("foreign-cancellation")]
    public async Task Without_caller_cancellation_the_same_internal_failures_stay_unavailable(
        string outcome)
    {
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var storeContext = CreateContext(
            new FailingStore(Failure(outcome, foreign.Token)),
            CancellationToken.None,
            Body($$"""{"code":"{{SentinelCode}}"}"""));
        var bodyContext = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            Body("{}"));
        bodyContext.Request.Body = new FailingBody(Failure(outcome, foreign.Token));
        var executor = new RecordingExecutor(outcome, abort: null, foreign.Token);
        var executorContext = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        var fromStore = await SetupHandlers.CompleteAsync(
            storeContext,
            new RecordingExecutor().Execute);
        var fromBody = await SetupHandlers.CompleteAsync(
            bodyContext,
            new RecordingExecutor().Execute);
        var fromExecutor = await SetupHandlers.CompleteAsync(
            executorContext,
            executor.Execute);

        Assert.Same(SetupResult.Unavailable, fromStore);
        Assert.Same(SetupResult.Unavailable, fromBody);
        Assert.Same(SetupResult.Unavailable, fromExecutor);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("credential-invalid")]
    [InlineData("conflict")]
    [InlineData("validation-failed")]
    [InlineData("unavailable")]
    [InlineData("null")]
    public async Task Without_caller_cancellation_the_executor_mapping_is_unchanged(string outcome)
    {
        var executor = new RecordingExecutor(outcome, abort: null);
        var context = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        var result = await SetupHandlers.CompleteAsync(context, executor.Execute);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(Expected(outcome), Describe(result));
    }

    [Fact]
    public async Task Without_caller_cancellation_the_fixed_rejections_are_unchanged()
    {
        var executor = new RecordingExecutor();
        var completed = CreateContext(
            new FixedStore(InstallationStatus.Completed),
            CancellationToken.None,
            Body($$"""{"code":"{{SentinelCode}}"}"""));
        var invalidBody = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            Body("""{"code":"too-short"}"""));
        var noStore = CreateContext(store: null, CancellationToken.None, Body("{}"));
        var pending = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            body: null);

        var replay = await SetupHandlers.CompleteAsync(completed, executor.Execute);
        var rejected = await SetupHandlers.CompleteAsync(invalidBody, executor.Execute);
        var unavailable = await SetupHandlers.CompleteAsync(noStore, executor.Execute);
        var status = await SetupHandlers.StatusAsync(pending);

        Assert.Equal("conflict", Describe(replay));
        Assert.Equal("invalid-request", Describe(rejected));
        Assert.Same(SetupResult.Unavailable, unavailable);
        Assert.Same(SetupResult.Pending, status);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task The_normalized_cancellation_carries_nothing_from_the_request()
    {
        using var abort = new CancellationTokenSource();
        var executor = new RecordingExecutor("failure", abort, secret: InjectedSecret);
        var context = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            abort.Token,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        var completion = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SetupHandlers.CompleteAsync(context, executor.Execute));

        AssertCallersCancellation(completion, abort.Token);
        Assert.Null(completion.InnerException);
        Assert.DoesNotContain(SentinelCode, completion.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(InjectedSecret, completion.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_concurrent_requests_never_share_a_token_or_a_result()
    {
        using var abort = new CancellationTokenSource();
        using var barrier = new Barrier(2);
        var cancelledExecutor = new RecordingExecutor("committed", abort, barrier: barrier);
        var completingExecutor = new RecordingExecutor("committed", abort: null, barrier: barrier);
        var cancelledContext = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            abort.Token,
            Body($$"""{"code":"{{SentinelCode}}"}"""));
        var completingContext = CreateContext(
            new FixedStore(InstallationStatus.PendingSetup),
            CancellationToken.None,
            Body($$"""{"code":"{{SentinelCode}}"}"""));

        // An explicit barrier inside both executors, so the overlap is arranged rather than timed.
        var cancelled = Task.Run(
            async () => await SetupHandlers.CompleteAsync(
                cancelledContext,
                cancelledExecutor.Execute),
            Token);
        var completing = Task.Run(
            async () => await SetupHandlers.CompleteAsync(
                completingContext,
                completingExecutor.Execute),
            Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        var result = await completing;

        AssertCallersCancellation(failure, abort.Token);
        Assert.Same(SetupResult.NoContent, result);
        Assert.Equal(1, cancelledExecutor.Calls);
        Assert.Equal(1, completingExecutor.Calls);
    }

    [Fact]
    public async Task The_http_entry_keeps_its_success_and_cancellation_behaviour()
    {
        await using var host = await Host.StartAsync();

        using var success = await host.CompleteAsync();
        using var abort = new CancellationTokenSource();
        host.Hold = true;
        var pending = host.CompleteAsync(abort);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await abort.CancelAsync();

        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        host.Release();
    }

    private static void AssertCallersCancellation(
        OperationCanceledException exception,
        CancellationToken expected)
    {
        Assert.Equal(expected, exception.CancellationToken);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
    }

    private static Exception Failure(string outcome, CancellationToken foreign) => outcome switch
    {
        "timeout" => new TimeoutException("internal timeout"),
        "foreign-cancellation" => new OperationCanceledException("internal", foreign),
        _ => new InvalidOperationException("internal failure"),
    };

    private static string Expected(string outcome) => outcome switch
    {
        "committed" => "no-content",
        "credential-invalid" => "credential-invalid",
        "conflict" => "conflict",
        "validation-failed" => "invalid-request",
        _ => "unavailable",
    };

    private static string Describe(IResult result) =>
        ReferenceEquals(result, SetupResult.NoContent) ? "no-content"
        : ReferenceEquals(result, SetupResult.CredentialInvalid) ? "credential-invalid"
        : ReferenceEquals(result, SetupResult.Unavailable) ? "unavailable"
        : ReferenceEquals(result, SetupResult.Pending) ? "pending"
        : ReferenceEquals(result, SetupResult.Completed) ? "completed"
        : ReferenceEquals(result, ManagementApiProblemResult.Conflict) ? "conflict"
        : ReferenceEquals(result, ManagementApiProblemResult.InvalidRequest)
            ? "invalid-request"
            : result.GetType().Name;

    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static DefaultHttpContext CreateContext(
        IServiceInstallationStore? store,
        CancellationToken requestAborted,
        Stream? body)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Service);
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            RequestAborted = requestAborted,
        };
        context.Request.Method = body is null ? HttpMethods.Get : HttpMethods.Post;
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
        }

        return context;
    }

    /// <summary>Cancels the caller while it answers, then produces one finite store outcome.</summary>
    private sealed class CancellingStore(string outcome, CancellationTokenSource abort)
        : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            abort.Cancel();
            return outcome switch
            {
                "pending" => ValueTask.FromResult<ServiceInstallationState?>(
                    ServiceInstallationState.CreatePending(serviceId)),
                "completed" => ValueTask.FromResult<ServiceInstallationState?>(
                    ServiceInstallationState.CreatePending(serviceId).Complete()),
                "absent" => ValueTask.FromResult<ServiceInstallationState?>(null),
                "foreign-cancellation" => ValueTask.FromException<ServiceInstallationState?>(
                    new OperationCanceledException("internal", new CancellationTokenSource().Token)),
                _ => ValueTask.FromException<ServiceInstallationState?>(
                    new InvalidOperationException("internal failure")),
            };
        }

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedStore(InstallationStatus? status) : IServiceInstallationStore
    {
        private int reads;

        internal int Reads => Volatile.Read(ref reads);

        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(status switch
            {
                null => null,
                InstallationStatus.Completed =>
                    ServiceInstallationState.CreatePending(serviceId).Complete(),
                _ => ServiceInstallationState.CreatePending(serviceId),
            });
        }

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingStore(Exception failure) : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ServiceInstallationState?>(failure);

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Cancels the caller during the body read, then produces one finite outcome.</summary>
    private sealed class CancellingBody(string outcome, CancellationTokenSource abort) : Stream
    {
        private bool answered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            abort.Cancel();
            if (answered)
            {
                return ValueTask.FromResult(0);
            }

            answered = true;
            switch (outcome)
            {
                case "valid":
                    return ValueTask.FromResult(Write(buffer, $$"""{"code":"{{SentinelCode}}"}"""));
                case "unusable":
                    return ValueTask.FromResult(Write(buffer, """{"code":"too-short"}"""));
                case "foreign-cancellation":
                    return ValueTask.FromException<int>(new OperationCanceledException(
                        "internal",
                        new CancellationTokenSource().Token));
                default:
                    return ValueTask.FromException<int>(new IOException("internal failure"));
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static int Write(Memory<byte> buffer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            bytes.CopyTo(buffer.Span);
            return bytes.Length;
        }
    }

    private sealed class FailingBody(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Counts its calls and produces one finite executor outcome.</summary>
    private sealed class RecordingExecutor(
        string outcome = "committed",
        CancellationTokenSource? abort = null,
        CancellationToken foreignToken = default,
        string? secret = null,
        Barrier? barrier = null)
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal ValueTask<SetupCompletionResult> Execute(
            HttpContext context,
            SetupCode setupCode,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            barrier?.SignalAndWait(TimeSpan.FromSeconds(5));
            abort?.Cancel();
            return outcome switch
            {
                "committed" => ValueTask.FromResult(SetupCompletionResult.Committed()),
                "credential-invalid" => ValueTask.FromResult(
                    SetupCompletionResult.CredentialInvalid()),
                "conflict" => ValueTask.FromResult(SetupCompletionResult.Conflict()),
                "validation-failed" => ValueTask.FromResult(
                    SetupCompletionResult.ValidationFailed()),
                "unavailable" => ValueTask.FromResult(
                    SetupCompletionResult.Unavailable()),
                "null" => ValueTask.FromResult<SetupCompletionResult>(null!),
                "timeout" => ValueTask.FromException<SetupCompletionResult>(
                    new TimeoutException("internal timeout")),
                "foreign-cancellation" => ValueTask.FromException<SetupCompletionResult>(
                    new OperationCanceledException(
                        "internal",
                        foreignToken == default ? new CancellationTokenSource().Token : foreignToken)),
                _ => ValueTask.FromException<SetupCompletionResult>(
                    new InvalidOperationException(
                        secret is null ? "internal failure" : $"internal failure {secret}")),
            };
        }
    }

    /// <summary>A host for this file's HTTP regression only.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private WebApplication? application;
        private HttpClient? client;

        private Host(WebApplication application, string root)
        {
            this.application = application;
            Root = root;
        }

        internal string Root { get; }

        internal bool Hold { get; set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HttpClient Client =>
            client ?? throw new InvalidOperationException("The host was not started.");

        internal static async Task<Host> StartAsync()
        {
            var root = ManagementApiDefaults.DefaultRootPath;
            var builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var mantle = builder.Services.AddServiceMantle(
                Service,
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.0");
            mantle.AddSensitiveHeaders();
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting();
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1(options => options.RootPath = root);
            mantle.AddServiceMantleManagementEntries();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new FixedHealth());
            builder.Services.AddScoped<IServiceInstallationStore>(
                _ => new FixedStore(InstallationStatus.PendingSetup));

            var application = builder.Build();
            var host = new Host(application, root);
            try
            {
                application.UseServiceMantlePipeline();
                application.MapServiceMantleSetup(host.ExecuteAsync);
            }
            catch (Exception)
            {
                await application.DisposeAsync();
                throw;
            }

            await application.StartAsync(Token);
            host.client = application.GetTestClient();
            return host;
        }

        internal void Release() => Released.TrySetResult();

        internal Task<HttpResponseMessage> CompleteAsync(CancellationTokenSource? abort = null)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                Root + ManagementEntryDefaults.SetupPath);
            request.Headers.TryAddWithoutValidation(
                ManagementEntryDefaults.UnsafeRequestHeaderName,
                ManagementEntryDefaults.UnsafeRequestHeaderValue);
            request.Content = new StringContent(
                $$"""{"code":"{{SentinelCode}}"}""",
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));
            return Client.SendAsync(request, abort?.Token ?? Token);
        }

        public async ValueTask DisposeAsync()
        {
            Released.TrySetResult();
            client?.Dispose();
            client = null;
            if (application is not null)
            {
                await application.DisposeAsync();
                application = null;
            }
        }

        private async ValueTask<SetupCompletionResult> ExecuteAsync(
            HttpContext context,
            SetupCode setupCode,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Hold)
            {
                await Released.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            return SetupCompletionResult.Committed();
        }

        private sealed class FixedHealth : IServiceHealthSnapshotSource
        {
            public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(new ServiceHealthSnapshot(
                    ServiceStartupPhase.PendingSetup,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Reachable));
        }
    }
}

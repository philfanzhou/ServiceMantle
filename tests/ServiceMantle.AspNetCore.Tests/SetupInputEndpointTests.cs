using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
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

using Fixture = SetupHostFixture;

/// <summary>
/// Covers the Setup completion overload that also accepts one consumer-defined installation input
/// object next to the Setup Code.
/// </summary>
/// <remarks>
/// A literal <c>app.MapServiceMantleSetup(null)</c> is a deliberate CS0121 once both overloads
/// exist; that source-level difference cannot be asserted at runtime and is documented in the
/// contract instead.
/// </remarks>
public sealed class SetupInputEndpointTests
{
    private const string Canary = "Canary-Pa55w0rd-Value";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Code => Fixture.SentinelCode;

    private static string InputBody(string input) => $$"""{"code":"{{Code}}","input":{{input}}}""";

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    private static async Task<Fixture> StartInputAsync(
        InstallationStatus installed = InstallationStatus.PendingSetup,
        SetupCompletionResult? completion = null,
        ILoggerProvider? loggerProvider = null)
    {
        var fixture = await Fixture.CreateAsync(
            installed: installed,
            completion: completion,
            phase: installed == InstallationStatus.Completed
                ? ServiceStartupPhase.Completed
                : ServiceStartupPhase.PendingSetup,
            inputMode: true,
            loggerProvider: loggerProvider);
        await fixture.StartAsync();
        return fixture;
    }

    [Fact]
    public async Task A_conforming_input_body_reaches_the_executor_exactly_once()
    {
        await using var fixture = await StartInputAsync();
        const string input = $$"""{"username":"admin","password":"{{Canary}}"}""";

        using var response = await fixture.CompleteAsync(body: InputBody(input));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, fixture.Executor.Calls);
        Assert.Equal([Code], fixture.Executor.Codes);
        Assert.Equal([input], fixture.Executor.Inputs);
    }

    [Theory]
    // Property order is free and the charset parameter is admitted.
    [InlineData("""{"input":{"workspace":"orders"},"code":"SentinelSetupCode0123456789_-ABC"}""", "application/json")]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-ABC\",\"input\":{}}", "application/json; charset=utf-8")]
    public async Task The_two_properties_are_matched_case_sensitively_in_any_order(
        string body,
        string contentType)
    {
        await using var fixture = await StartInputAsync();

        using var response = await fixture.CompleteAsync(body: body, contentType: contentType);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Theory]
    // Missing properties.
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC"}""")]
    [InlineData("""{"input":{}}""")]
    // Input is not an object.
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":[]}""")]
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":"{}"}""")]
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":null}""")]
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":1}""")]
    // Duplicates, case variants, and extra properties.
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","code":"SentinelSetupCode0123456789_-ABC","input":{}}""")]
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":{},"input":{}}""")]
    [InlineData("""{"Code":"SentinelSetupCode0123456789_-ABC","Input":{}}""")]
    [InlineData("""{"code":"SentinelSetupCode0123456789_-ABC","input":{},"extra":1}""")]
    // Code shape.
    [InlineData("""{"code":123,"input":{}}""")]
    [InlineData("""{"code":{"value":"x"},"input":{}}""")]
    [InlineData("""{"code":"short","input":{}}""")]
    [InlineData("""{"code":null,"input":{}}""")]
    // Document shape.
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-ABC\",\"input\":{},}")]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-ABC\",\"input\":{}} trailing")]
    [InlineData("// c\r\n{\"code\":\"SentinelSetupCode0123456789_-ABC\",\"input\":{}}")]
    [InlineData("{\"code\":\"SentinelSetupCode0123456789_-ABC\"/*c*/,\"input\":{}}")]
    public async Task Every_unusable_input_shape_is_the_fixed_invalid_request(string body)
    {
        await using var fixture = await StartInputAsync();

        using var response = await fixture.CompleteAsync(body: body);
        var raw = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ManagementApiDefaults.InvalidRequestErrorCode,
            raw,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Code, raw, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Theory]
    // Media type, query string, and content encoding.
    [InlineData("text/plain", null, null)]
    [InlineData(null, null, null)]
    [InlineData("application/json", "?force=1", null)]
    [InlineData("application/json", null, "gzip")]
    public async Task Admission_rejections_never_read_the_body(
        string? contentType,
        string? query,
        string? contentEncoding)
    {
        await using var fixture = await StartInputAsync();

        using var response = await fixture.CompleteAsync(
            body: InputBody("""{"x":1}"""),
            contentType: contentType,
            path: query is null ? null : fixture.SetupPath + query,
            contentEncoding: contentEncoding);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task The_json_depth_limit_admits_eight_and_rejects_nine_levels()
    {
        await using var fixture = await StartInputAsync();
        // The reader counts the root object as depth zero: seven nested objects inside the input
        // object reach depth eight, which is admitted; eight nested objects reach depth nine.
        var within = InputBody("{\"a\":" + Repeat("{\"a\":", 6) + "1" + Repeat("}", 7));
        var beyond = InputBody("{\"a\":" + Repeat("{\"a\":", 7) + "1" + Repeat("}", 8));

        using var deep = await fixture.CompleteAsync(body: beyond);
        using var exact = await fixture.CompleteAsync(body: within);

        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, exact.StatusCode);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Fact]
    public async Task The_body_limit_is_enforced_by_the_declared_length_and_the_actual_bytes()
    {
        await using var fixture = await StartInputAsync();
        // The declared length exceeds the input mode limit, so the body is never read.
        var declared = InputBody("""{"pad":"<over>"}""").Replace(
            "<over>",
            new string('p', SetupRequestParser.MaximumInputBodyLength));
        Assert.True(Encoding.UTF8.GetByteCount(declared) > SetupRequestParser.MaximumInputBodyLength);
        using var declaredResponse = await fixture.CompleteAsync(body: declared);

        // Without a declared length the same decision comes from the actual byte count.
        var undeclared = new UndeclaredLengthContent(
            Encoding.UTF8.GetBytes(declared));
        using var undeclaredRequest = new HttpRequestMessage(
            HttpMethod.Post,
            fixture.SetupPath);
        undeclaredRequest.Headers.TryAddWithoutValidation(
            ManagementEntryDefaults.UnsafeRequestHeaderName,
            ManagementEntryDefaults.UnsafeRequestHeaderValue);
        undeclaredRequest.Content = undeclared;
        using var undeclaredResponse = await fixture.Client.SendAsync(undeclaredRequest, Token);

        Assert.Equal(HttpStatusCode.BadRequest, declaredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, undeclaredResponse.StatusCode);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task A_body_of_exactly_the_limit_passes_the_size_gate()
    {
        await using var fixture = await StartInputAsync();
        // 9 + 32 + 18 + padding + 3 = 16 KiB exactly, all ASCII.
        const int fixedBytes = 9 + 32 + 18 + 3;
        var padding = new string(
            'p',
            SetupRequestParser.MaximumInputBodyLength - fixedBytes);
        var body = "{\"code\":\"" + Code + "\",\"input\":{\"pad\":\"" + padding + "\"}}";
        Assert.Equal(
            SetupRequestParser.MaximumInputBodyLength,
            Encoding.UTF8.GetByteCount(body));

        using var response = await fixture.CompleteAsync(body: body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, fixture.Executor.Calls);
        Assert.Single(fixture.Executor.Inputs, input => input.Contains("pad", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupCompletionStatus.CredentialInvalid, HttpStatusCode.Unauthorized)]
    [InlineData(SetupCompletionStatus.Conflict, HttpStatusCode.Conflict)]
    [InlineData(SetupCompletionStatus.ValidationFailed, HttpStatusCode.BadRequest)]
    [InlineData(SetupCompletionStatus.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Every_input_completion_status_maps_to_one_fixed_response(
        SetupCompletionStatus status,
        HttpStatusCode expected)
    {
        await using var fixture = await StartInputAsync(completion: Result(status));

        using var response = await fixture.CompleteAsync(body: InputBody("""{"x":1}"""));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Fact]
    public async Task A_null_result_an_executor_exception_and_an_internal_cancellation_are_unavailable()
    {
        const string secret = "contributor-secret-value";
        await using var nullResult = await StartInputAsync();
        nullResult.Executor.Result = null;
        await using var throwing = await StartInputAsync();
        throwing.Executor.Failure = new InvalidOperationException(secret);
        await using var internalCancellation = await StartInputAsync();
        internalCancellation.Executor.Failure = new OperationCanceledException(
            "internal setup cancellation secret",
            new CancellationTokenSource(0).Token);

        using var withNull = await nullResult.CompleteAsync(body: InputBody("{}"));
        using var withException = await throwing.CompleteAsync(body: InputBody("{}"));
        using var withCancellation = await internalCancellation.CompleteAsync(body: InputBody("{}"));

        foreach (var response in new[] { withNull, withException, withCancellation })
        {
            var raw = await AssertUnavailableAsync(response);
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_completed_installation_is_a_conflict_that_never_reads_the_input_body()
    {
        await using var fixture = await StartInputAsync(installed: InstallationStatus.Completed);

        using var response = await fixture.CompleteAsync(body: "not json at all", contentType: "text/plain");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, fixture.Executor.Calls);
        // The body itself was never consumed: the direct invocation below reads zero bytes.
        var body = new CountingBody("""{"code":"whatever","input":{}}""");
        var context = Context(store: new CompletedStore());
        context.Request.Body = body;

        var read = await SetupHandlers.CompleteWithInputAsync(
            context,
            new DirectExecutor(SetupCompletionResult.Committed()).Execute);

        Assert.Same(ManagementApiProblemResult.Conflict, read);
        Assert.Equal(0, body.Reads);
    }

    [Fact]
    public async Task A_missing_absent_or_failing_installation_authority_is_unavailable()
    {
        var unbuilt = await Fixture.CreateAsync(registerStore: false, inputMode: true);
        await unbuilt.StartAsync();
        await using var withoutStore = unbuilt;
        using var noStore = await withoutStore.CompleteAsync(body: InputBody("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noStore.StatusCode);
        Assert.Equal(0, withoutStore.Executor.Calls);

        await using var failing = await StartInputAsync();
        failing.Store.Failure = new InvalidOperationException("connection-secret");
        using var response = await failing.CompleteAsync(body: InputBody("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, failing.Executor.Calls);
    }

    [Fact]
    public async Task The_input_is_released_after_the_executor_returns_or_throws()
    {
        await using var committed = await StartInputAsync();
        using var success = await committed.CompleteAsync(
            body: InputBody($$"""{"password":"{{Canary}}"}"""));

        await using var rejected = await StartInputAsync(
            completion: SetupCompletionResult.ValidationFailed());
        using var invalid = await rejected.CompleteAsync(body: InputBody("{}"));

        await using var throwing = await StartInputAsync();
        throwing.Executor.Failure = new InvalidOperationException("consumer transaction failure");
        using var failed = await throwing.CompleteAsync(body: InputBody("{}"));

        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        foreach (var retained in new[]
                 {
                     Assert.Single(committed.Executor.RetainedInputs),
                     Assert.Single(rejected.Executor.RetainedInputs),
                     Assert.Single(throwing.Executor.RetainedInputs),
                 })
        {
            Assert.Throws<ObjectDisposedException>(() => retained.RootElement);
            Assert.DoesNotContain(Canary, retained.ToString(), StringComparison.Ordinal);
            Assert.Equal("SetupInput(********)", retained.ToString());
        }
    }

    [Fact]
    public async Task The_parser_returns_one_zeroed_buffer_for_every_rejection()
    {
        var pool = new RecordingPool();
        var rejected = await SetupRequestParser.ParseWithInputAsync(
            Context("""{"code":"short","input":{}}"""),
            pool);

        Assert.Null(rejected);
        pool.AssertAllReturnedAndCleared(rentCount: 1);
    }

    [Fact]
    public async Task The_parser_returns_one_zeroed_buffer_for_an_oversized_actual_body()
    {
        var pool = new RecordingPool();
        var oversized = Encoding.UTF8.GetBytes(
            InputBody("{\"pad\":\"" + new string('p', SetupRequestParser.MaximumInputBodyLength) + "\"}"));
        var context = Context();
        context.Request.Body = new MemoryStream(oversized);

        var rejected = await SetupRequestParser.ParseWithInputAsync(context, pool);

        Assert.Null(rejected);
        pool.AssertAllReturnedAndCleared(rentCount: 1);
    }

    [Fact]
    public async Task The_handler_returns_the_zeroed_input_buffer_after_the_executor_returns()
    {
        var pool = new RecordingPool();
        var executor = new DirectExecutor(SetupCompletionResult.Committed());
        var context = Context(InputBody("""{"x":1}"""), store: new PendingStore());

        var result = await SetupHandlers.CompleteWithInputAsync(context, executor.Execute, pool);

        Assert.Same(SetupResult.NoContent, result);
        Assert.Equal(1, executor.Calls);
        pool.AssertAllReturnedAndCleared(rentCount: 1);
    }

    [Fact]
    public async Task The_handler_returns_the_zeroed_input_buffer_after_the_executor_throws()
    {
        var pool = new RecordingPool();
        var executor = new DirectExecutor(SetupCompletionResult.Committed())
        {
            Failure = new InvalidOperationException("consumer failure"),
        };
        var context = Context(InputBody("""{"x":1}"""), store: new PendingStore());

        var result = await SetupHandlers.CompleteWithInputAsync(context, executor.Execute, pool);

        Assert.Same(SetupResult.Unavailable, result);
        Assert.Equal(1, executor.Calls);
        pool.AssertAllReturnedAndCleared(rentCount: 1);
    }

    [Fact]
    public async Task The_input_never_reaches_a_log_at_any_level()
    {
        var logs = new CapturingLoggerProvider();
        await using var committed = await StartInputAsync(loggerProvider: logs);
        await using var rejected = await StartInputAsync(
            completion: SetupCompletionResult.ValidationFailed(),
            loggerProvider: logs);
        await using var failing = await StartInputAsync(loggerProvider: logs);
        failing.Executor.Failure = new InvalidOperationException($"failure with {Canary}");

        using var success = await committed.CompleteAsync(body: InputBody($$"""{"password":"{{Canary}}"}"""));
        using var invalid = await rejected.CompleteAsync(body: InputBody($$"""{"password":"{{Canary}}"}"""));
        using var failure = await failing.CompleteAsync(body: InputBody($$"""{"password":"{{Canary}}"}"""));

        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        foreach (var message in logs.Messages)
        {
            Assert.DoesNotContain(Canary, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Two_concurrent_input_requests_each_keep_their_own_input()
    {
        await using var fixture = await StartInputAsync();
        fixture.Executor.ExpectedCalls = 2;
        fixture.Executor.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Selector = call => call == 1
            ? SetupCompletionResult.Committed()
            : SetupCompletionResult.Conflict();

        var first = fixture.CompleteAsync(body: InputBody("""{"who":"first"}"""));
        var second = fixture.CompleteAsync(body: InputBody("""{"who":"second"}"""));
        await fixture.Executor.Entered.Task.WaitAsync(Token);
        fixture.Executor.Gate.SetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;

        Assert.Equal(2, fixture.Executor.Calls);
        Assert.Equal(
            new[] { """{"who":"first"}""", """{"who":"second"}""" },
            fixture.Executor.Inputs.Order().ToArray());
        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };
        Assert.Single(statuses, status => status == HttpStatusCode.NoContent);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Each_overload_maps_once_and_mixed_mapping_still_fails_at_most_once()
    {
        await using var codeMode = await Fixture.StartAsync();
        await using var inputMode = await StartInputAsync();

        using var codeResponse = await codeMode.CompleteAsync();
        using var inputResponse = await inputMode.CompleteAsync(body: InputBody("{}"));
        Assert.Equal(HttpStatusCode.NoContent, codeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, inputResponse.StatusCode);

        // Both orders of mapping the two overloads on one host fail with the one fixed message.
        var mixed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DualMappingHost.StartAsync(codeFirst: true));
        var reversed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DualMappingHost.StartAsync(codeFirst: false));
        Assert.Equal(SetupMapping.Failure().Message, mixed.Message);
        Assert.Equal(SetupMapping.Failure().Message, reversed.Message);
    }

    [Fact]
    public async Task The_input_overload_requires_an_explicit_executor()
    {
        var withoutExecutor = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.CreateAsync(inputMode: true, mapExecutor: false));

        Assert.Equal(SetupMapping.MissingExecutor().Message, withoutExecutor.Message);
    }

    private static DefaultHttpContext Context(
        string body = "{}",
        IServiceInstallationStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ServiceId.Parse("catalog"));
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            RequestAborted = CancellationToken.None,
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static async Task<string> AssertUnavailableAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"errorCode\":\"management.setup.unavailable\"}", raw);
        return raw;
    }

    private static SetupCompletionResult Result(SetupCompletionStatus status) =>
        status switch
        {
            SetupCompletionStatus.Committed => SetupCompletionResult.Committed(),
            SetupCompletionStatus.CredentialInvalid => SetupCompletionResult.CredentialInvalid(),
            SetupCompletionStatus.Conflict => SetupCompletionResult.Conflict(),
            SetupCompletionStatus.ValidationFailed => SetupCompletionResult.ValidationFailed(),
            _ => SetupCompletionResult.Unavailable(),
        };

    /// <summary>Sends bytes without a computable length, so no Content-Length is declared.</summary>
    private sealed class UndeclaredLengthContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            await stream.WriteAsync(bytes, Token);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Counts reads so a body that is never parsed shows zero reads.</summary>
    private sealed class CountingBody(string body) : Stream
    {
        private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(body));

        internal int Reads { get; private set; }

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
            Reads++;
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PendingStore : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServiceInstallationState?>(
                ServiceInstallationState.CreatePending(serviceId));

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompletedStore : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServiceInstallationState?>(
                ServiceInstallationState.CreatePending(serviceId).Complete());

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DirectExecutor(SetupCompletionResult result)
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal Exception? Failure { get; set; }

        internal ValueTask<SetupCompletionResult> Execute(
            HttpContext context,
            SetupCode setupCode,
            SetupInput input,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("""{"x":1}""", input.RootElement.GetRawText());
            if (Failure is { } failure)
            {
                return ValueTask.FromException<SetupCompletionResult>(failure);
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Records rents and returns and asserts every buffer came back cleared, once.</summary>
    private sealed class RecordingPool : ArrayPool<byte>
    {
        private readonly List<byte[]> rented = [];
        private readonly List<(byte[] Buffer, bool Cleared)> returned = [];

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength];
            lock (rented)
            {
                rented.Add(buffer);
            }

            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            var alreadyCleared = Array.TrueForAll(array, static value => value == 0);
            lock (returned)
            {
                returned.Add((array, alreadyCleared));
            }
        }

        internal void AssertAllReturnedAndCleared(int rentCount)
        {
            lock (rented)
            {
                Assert.Equal(rentCount, rented.Count);
                lock (returned)
                {
                    Assert.Equal(rentCount, returned.Count);
                    foreach (var (buffer, cleared) in returned)
                    {
                        Assert.Contains(buffer, rented);
                        Assert.True(cleared, "A buffer was returned without being zeroed first.");
                    }
                }
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception) + exception);
        }
    }

    /// <summary>Maps both overloads on one host, in either order, to prove the shared limit.</summary>
    private sealed class DualMappingHost
    {
        internal static async Task<DualMappingHost> StartAsync(bool codeFirst)
        {
            var builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var mantle = builder.Services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.0");
            mantle.AddSensitiveHeaders();
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting();
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1();
            mantle.AddServiceMantleManagementEntries();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new FixedHealth());
            builder.Services.AddScoped<IServiceInstallationStore>(
                _ => new CompletedStore());

            var application = builder.Build();
            DualMappingHost? host = null;
            try
            {
                application.UseServiceMantlePipeline();
                if (codeFirst)
                {
                    application.MapServiceMantleSetup(CodeExecutor);
                    application.MapServiceMantleSetup(InputExecutor);
                }
                else
                {
                    application.MapServiceMantleSetup(InputExecutor);
                    application.MapServiceMantleSetup(CodeExecutor);
                }

                await application.StartAsync(Token);
                host = new DualMappingHost();
                return host;
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        private static ValueTask<SetupCompletionResult> CodeExecutor(
            HttpContext context,
            SetupCode setupCode,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SetupCompletionResult.Committed());

        private static ValueTask<SetupCompletionResult> InputExecutor(
            HttpContext context,
            SetupCode setupCode,
            SetupInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SetupCompletionResult.Committed());

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

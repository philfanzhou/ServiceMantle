using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Session;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Pins the login body admission: the envelope is decided by the bytes that actually arrive, the
/// adapter reads the admitted copy through whichever reader it asks for, and the request's own body
/// and pipe feature come back afterwards.
/// </summary>
/// <remarks>
/// The handler is called directly for everything a fake context can prove, so the assertions are
/// about what the admission decided rather than about what a client happened to observe. The one
/// thing a fake context cannot prove - a real unknown-length request on the wire - runs against a
/// loopback Kestrel host in this file, which frames the request itself instead of letting an HTTP
/// client add a length header.
/// </remarks>
public sealed class ManagementSessionBodyAdmissionTests
{
    private const int Limit = (int)ManagementSessionMapping.MaximumLoginBodyLength;

    private const int Probe = ManagementSessionBodyAdmission.ProbeLength;

    private const string Sentinel = "sentinel-login-body-credential";

    private static readonly TimeSpan Budget = ManagementSessionOptions.MaximumLoginTimeout;

    /// <summary>The ways an adapter can read the body it was handed.</summary>
    public enum ReadStyle
    {
        SyncRead,
        ReadByte,
        ReadAsyncMemory,
        ReadAsyncArray,
        CopyToAsync,
        StreamReader,
        BodyReader,
        None,
        Prefix
    }

    /// <summary>The seven read styles that consume the whole body.</summary>
    private static readonly ReadStyle[] FullReads =
    [
        ReadStyle.SyncRead,
        ReadStyle.ReadByte,
        ReadStyle.ReadAsyncMemory,
        ReadStyle.ReadAsyncArray,
        ReadStyle.CopyToAsync,
        ReadStyle.StreamReader,
        ReadStyle.BodyReader
    ];

    /// <summary>The host request-size features a login can meet.</summary>
    public enum HostLimit
    {
        Missing,
        ReadOnlySmaller,
        WritableUnset,
        WritableSmaller,
        WritableLarger
    }

    public static TheoryData<bool, int> Lengths => new()
    {
        { true, 0 }, { true, Limit }, { true, Probe },
        { false, 0 }, { false, Limit }, { false, Probe },
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public async Task The_envelope_counts_the_bytes_that_arrive_whatever_the_length_declares(
        bool declared,
        int length)
    {
        // A single large read and a stream that only ever answers small pieces are the two shapes a
        // body arrives in, and neither may change the admitted envelope.
        foreach (var maxRead in new[] { int.MaxValue, 7 })
        {
            using var scope = new LoginScope();
            var content = Body(length);
            var stream = scope.Send(content, maxRead, declared);
            var adapter = new RecordingAdapter();

            var outcome = await scope.LoginAsync(adapter);

            if (length > Limit)
            {
                Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
                Assert.Equal(0, adapter.Calls);
                Assert.Equal(0, scope.Authentication.SignInCalls);

                // A declared oversize is refused before a single byte is read; an undeclared one
                // costs exactly the one byte that proves the overrun and nothing beyond it.
                Assert.Equal(declared ? 0 : Probe, stream.BytesRead);
            }
            else
            {
                Assert.Equal(StatusCodes.Status204NoContent, outcome.StatusCode);
                Assert.Equal(length, stream.BytesRead);
                Assert.Equal(content, Assert.Single(adapter.Bodies));
            }

            Assert.True(stream.BytesRead <= Probe);
            Assert.DoesNotContain(Sentinel, outcome.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_declared_length_under_the_limit_does_not_excuse_the_bytes_that_follow_it()
    {
        using var scope = new LoginScope();
        var stream = scope.Send(Body(Probe), int.MaxValue, declared: false);

        // The request claims to be tiny and sends one byte past the envelope anyway.
        scope.Context.Request.ContentLength = 10;
        var adapter = new RecordingAdapter();

        var outcome = await scope.LoginAsync(adapter);

        Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
        Assert.Equal(Probe, stream.BytesRead);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task The_envelope_is_counted_in_bytes_and_not_in_characters()
    {
        var oversized = MultiByteBody(Limit + 2);
        var admitted = MultiByteBody(Limit);
        Assert.True(Encoding.UTF8.GetString(oversized).Length < Limit);

        using var rejecting = new LoginScope();
        rejecting.Send(oversized, int.MaxValue, declared: false);
        var rejected = new RecordingAdapter();
        using var admitting = new LoginScope();
        admitting.Send(admitted, int.MaxValue, declared: true);
        var accepted = new RecordingAdapter();

        var refusal = await rejecting.LoginAsync(rejected);
        var success = await admitting.LoginAsync(accepted);

        Assert.Equal(StatusCodes.Status400BadRequest, refusal.StatusCode);
        Assert.Equal(0, rejected.Calls);
        Assert.Equal(StatusCodes.Status204NoContent, success.StatusCode);
        Assert.Equal(admitted, Assert.Single(accepted.Bodies));
    }

    [Theory]
    [InlineData(HostLimit.Missing)]
    [InlineData(HostLimit.ReadOnlySmaller)]
    [InlineData(HostLimit.WritableUnset)]
    [InlineData(HostLimit.WritableSmaller)]
    [InlineData(HostLimit.WritableLarger)]
    public async Task No_host_request_size_feature_widens_or_raises_the_envelope(HostLimit host)
    {
        using var rejecting = new LoginScope();
        var feature = rejecting.UseHostLimit(host);
        rejecting.Send(Body(Probe), int.MaxValue, declared: false);
        var rejected = new RecordingAdapter();
        using var admitting = new LoginScope();
        admitting.UseHostLimit(host);
        admitting.Send(Body(Limit), int.MaxValue, declared: false);
        var accepted = new RecordingAdapter();

        var refusal = await rejecting.LoginAsync(rejected);
        var success = await admitting.LoginAsync(accepted);

        Assert.Equal(StatusCodes.Status400BadRequest, refusal.StatusCode);
        Assert.Equal(0, rejected.Calls);
        Assert.Equal(0, rejecting.Authentication.SignInCalls);
        Assert.DoesNotContain(Sentinel, refusal.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status204NoContent, success.StatusCode);
        Assert.Equal(Limit, Assert.Single(accepted.Bodies).Length);

        // The host's own limit is left exactly as the host set it. Widening it would break the
        // envelope, and narrowing it would reject a body inside the envelope: a server that counts
        // a chunked request counts its framing too, so 64 KiB of content is more than 64 KiB there.
        long? expected = host switch
        {
            HostLimit.ReadOnlySmaller or HostLimit.WritableSmaller => 1024L,
            HostLimit.WritableLarger => 30L * 1024 * 1024,
            _ => null,
        };
        Assert.Equal(expected, feature?.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(ReadStyle.SyncRead)]
    [InlineData(ReadStyle.ReadByte)]
    [InlineData(ReadStyle.ReadAsyncMemory)]
    [InlineData(ReadStyle.ReadAsyncArray)]
    [InlineData(ReadStyle.CopyToAsync)]
    [InlineData(ReadStyle.StreamReader)]
    [InlineData(ReadStyle.BodyReader)]
    public async Task Every_supported_adapter_read_sees_the_original_body_in_full(ReadStyle style)
    {
        using var scope = new LoginScope();
        var content = MultiByteBody(Limit);
        scope.Send(content, maxRead: 13, declared: false);
        var adapter = new RecordingAdapter { Style = style };

        var outcome = await scope.LoginAsync(adapter);

        Assert.Equal(StatusCodes.Status204NoContent, outcome.StatusCode);
        Assert.Equal(content, Assert.Single(adapter.Bodies));
        Assert.Equal(1, scope.Authentication.SignInCalls);
    }

    [Theory]
    [InlineData(ReadStyle.None)]
    [InlineData(ReadStyle.Prefix)]
    public async Task An_adapter_that_reads_nothing_or_a_prefix_cannot_admit_an_oversized_body(
        ReadStyle style)
    {
        using var scope = new LoginScope();
        var stream = scope.Send(Body(Probe), int.MaxValue, declared: false);
        var adapter = new RecordingAdapter { Style = style };

        var outcome = await scope.LoginAsync(adapter);

        // The overrun is proved before the adapter is reached, so how much it would have read
        // cannot change the answer.
        Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(Probe, stream.BytesRead);
        Assert.Equal(0, scope.Authentication.SignInCalls);
    }

    [Fact]
    public async Task The_request_body_and_pipe_feature_come_back_after_success_failure_and_cancellation()
    {
        foreach (var failure in new Exception?[] { null, new InvalidOperationException(Sentinel) })
        {
            foreach (var withFeature in new[] { true, false })
            {
                using var scope = new LoginScope();
                var original = scope.Send(Body(64), int.MaxValue, declared: true);
                var originalPipe = withFeature ? scope.UseOriginalPipe() : null;
                var adapter = new RecordingAdapter { Style = ReadStyle.BodyReader, Failure = failure };

                var outcome = await scope.LoginAsync(adapter);

                Assert.Equal(
                    failure is null
                        ? StatusCodes.Status204NoContent
                        : StatusCodes.Status503ServiceUnavailable,
                    outcome.StatusCode);
                Assert.NotSame(original, adapter.SeenBody);
                Assert.Same(original, scope.Context.Request.Body);
                Assert.Same(originalPipe, scope.Context.Features.Get<IRequestBodyPipeFeature>());

                // The host still owns its stream and its reader: this handler released only the
                // copy it created.
                Assert.False(original.Disposed);
                Assert.False(originalPipe?.Completed ?? false);
            }
        }
    }

    [Fact]
    public async Task A_cancelled_login_also_puts_the_request_body_back()
    {
        using var scope = new LoginScope();
        var original = scope.Send(Body(64), int.MaxValue, declared: true);
        var originalPipe = scope.UseOriginalPipe();

        await Assert.ThrowsAsync<OperationCanceledException>(() => scope.RunAsync(
            async (_, token) =>
            {
                await scope.Abort.CancelAsync();
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return null!;
            }));

        Assert.Same(original, scope.Context.Request.Body);
        Assert.Same(originalPipe, scope.Context.Features.Get<IRequestBodyPipeFeature>());
        Assert.False(original.Disposed);
        Assert.False(originalPipe.Completed);
    }

    [Fact]
    public async Task Concurrent_logins_never_share_an_admitted_buffer()
    {
        var scopes = Enumerable.Range(0, 8).Select(_ => new LoginScope()).ToArray();
        try
        {
            var rendezvous = new AdapterRendezvous(scopes.Length);
            var adapters = new RecordingAdapter[scopes.Length];
            var bodies = new byte[scopes.Length][];
            for (var index = 0; index < scopes.Length; index++)
            {
                bodies[index] = Encoding.UTF8.GetBytes(
                    "{\"password\":\"" + Sentinel + index +
                        new string((char)('a' + index), 4096) + "\"}");
                scopes[index].Send(bodies[index], maxRead: 97, declared: index % 2 == 0);
                adapters[index] = new RecordingAdapter
                {
                    Style = FullReads[index % FullReads.Length],
                    OnEntered = rendezvous.EnterAsync,
                };
            }

            var pending = scopes
                .Select((scope, index) => scope.LoginAsync(adapters[index]))
                .ToArray();
            try
            {
                await rendezvous.AllEntered.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken);

                // Every adapter is holding its installed copy at the same time. None can read or
                // return until this checkpoint releases them, so sequential completion cannot make
                // the isolation assertion pass accidentally.
                Assert.All(pending, task => Assert.False(task.IsCompleted));
                Assert.All(adapters, adapter => Assert.NotNull(adapter.SeenBody));
                Assert.Equal(
                    scopes.Length,
                    adapters.Select(adapter => adapter.SeenBody).Distinct().Count());
            }
            finally
            {
                rendezvous.Release();
            }

            var outcomes = await Task.WhenAll(pending)
                .WaitAsync(TestContext.Current.CancellationToken);

            for (var index = 0; index < scopes.Length; index++)
            {
                Assert.Equal(StatusCodes.Status204NoContent, outcomes[index].StatusCode);
                Assert.Equal(bodies[index], Assert.Single(adapters[index].Bodies));
            }
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_body_read_failure_is_the_fixed_unavailable_and_a_413_is_the_fixed_rejection()
    {
        var cases = new (Exception Failure, int StatusCode)[]
        {
            (new IOException(Sentinel), StatusCodes.Status503ServiceUnavailable),
            (
                new BadHttpRequestException(Sentinel, StatusCodes.Status413PayloadTooLarge),
                StatusCodes.Status400BadRequest),
            (
                // Only the host's explicit "too large" is the request's fault; every other rejection
                // it raises stays the safe unavailable result.
                new BadHttpRequestException(Sentinel, StatusCodes.Status400BadRequest),
                StatusCodes.Status503ServiceUnavailable),
            (
                new OperationCanceledException(Sentinel, new CancellationTokenSource().Token),
                StatusCodes.Status503ServiceUnavailable),
        };

        foreach (var (failure, statusCode) in cases)
        {
            using var scope = new LoginScope();
            var stream = scope.Send(Body(4096), int.MaxValue, declared: true);
            stream.FailAfterBytes = 1024;
            stream.Failure = failure;
            var adapter = new RecordingAdapter();

            var outcome = await scope.LoginAsync(adapter);

            Assert.Equal(statusCode, outcome.StatusCode);
            Assert.Equal(0, adapter.Calls);
            Assert.Equal(0, scope.Authentication.SignInCalls);
            Assert.DoesNotContain(Sentinel, outcome.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_body_read_that_spends_the_login_budget_never_reaches_the_adapter()
    {
        using var scope = new LoginScope();
        var stream = scope.Send(Body(4096), int.MaxValue, declared: true);

        // The stream cooperates with the token it was handed and nothing else, so the boundary is
        // the budget itself and not a guessed delay.
        stream.OnRead = WaitForCancellationAsync;
        var adapter = new RecordingAdapter();

        var outcome = await scope.LoginAsync(
            adapter,
            ManagementSessionOptions.MinimumLoginTimeout);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.StatusCode);
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(0, scope.Authentication.SignInCalls);
    }

    [Fact]
    public async Task Caller_cancellation_before_and_during_the_body_read_keeps_the_original_token()
    {
        using var before = new LoginScope();
        before.Send(Body(64), int.MaxValue, declared: true);
        await before.Abort.CancelAsync();
        var never = new RecordingAdapter();

        var early = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            before.RunAsync(never.InvokeAsync));

        Assert.Equal(before.Abort.Token, early.CancellationToken);
        Assert.Equal(0, never.Calls);

        using var during = new LoginScope();
        var stream = during.Send(Body(8192), maxRead: 512, declared: true);
        stream.OnRead = async token =>
        {
            // The caller aborts while the body is still arriving, and the read fails afterwards:
            // the caller's own cancellation still outranks that failure.
            await during.Abort.CancelAsync();
            await WaitForCancellationAsync(token);
        };
        stream.FailAfterBytes = 0;
        stream.Failure = new IOException(Sentinel);
        var adapter = new RecordingAdapter();

        var late = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            during.RunAsync(adapter.InvokeAsync));

        Assert.Equal(during.Abort.Token, late.CancellationToken);
        Assert.Null(late.InnerException);
        Assert.DoesNotContain(Sentinel, late.Message, StringComparison.Ordinal);
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(0, during.Authentication.SignInCalls);
    }

    [Fact]
    public async Task A_real_chunked_request_without_a_content_length_meets_the_same_envelope()
    {
        await using var host = await ChunkedLoginHost.StartAsync();

        var admitted = await host.SendChunkedAsync(Limit, chunkSize: 8192);
        var rejected = await host.SendChunkedAsync(Probe, chunkSize: 8192);

        // The framing is asserted on the exact bytes that went out: chunked, with no length header
        // for the server or this handler to trust.
        Assert.Contains("Transfer-Encoding: chunked", admitted.Request, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", admitted.Request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transfer-Encoding: chunked", rejected.Request, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", rejected.Request, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("HTTP/1.1 204", admitted.Response, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", rejected.Response, StringComparison.Ordinal);
        Assert.Equal(Limit, Assert.Single(host.Adapter.Bodies).Length);
        Assert.Equal(1, host.Adapter.Calls);
        Assert.DoesNotContain(Sentinel, rejected.Response, StringComparison.Ordinal);
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task;
    }

    /// <summary>
    /// Builds a body of exactly <paramref name="length"/> bytes carrying the sentinel.
    /// </summary>
    private static byte[] Body(int length)
    {
        if (length == 0)
        {
            return [];
        }

        var prefix = Encoding.UTF8.GetBytes("{\"password\":\"" + Sentinel);
        var body = new byte[length];
        prefix.AsSpan(0, Math.Min(prefix.Length, length)).CopyTo(body);
        for (var index = prefix.Length; index < length; index++)
        {
            body[index] = (byte)'p';
        }

        return body;
    }

    /// <summary>
    /// Builds a body of exactly <paramref name="byteLength"/> bytes out of three-byte characters, so
    /// its character count is nowhere near its byte count.
    /// </summary>
    private static byte[] MultiByteBody(int byteLength)
    {
        var character = Encoding.UTF8.GetBytes("€");
        var body = new byte[byteLength];
        var index = 0;
        while (byteLength - index >= character.Length)
        {
            character.CopyTo(body, index);
            index += character.Length;
        }

        while (index < byteLength)
        {
            body[index++] = (byte)'a';
        }

        return body;
    }

    private static ManagementIdentity Identity() => ManagementIdentity.Create(
        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
        "operator-1",
        [ManagementPermission.Read, ManagementPermission.Admin],
        "sensitive-display-name");

    /// <summary>What one login answered: the executed status code and the body it wrote.</summary>
    private readonly record struct LoginOutcome(int StatusCode, string Body);

    /// <summary>What one raw chunked exchange sent and received.</summary>
    private readonly record struct ChunkedExchange(string Request, string Response);

    /// <summary>
    /// Owns one handler call: the context, its request abort source, the response buffer, and the
    /// recorded authentication calls. Nothing here is shared with another test file.
    /// </summary>
    private sealed class LoginScope : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly MemoryStream response = new();

        internal LoginScope()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(Authentication);
            provider = services.BuildServiceProvider();
            Context = new DefaultHttpContext
            {
                RequestServices = provider,
                RequestAborted = Abort.Token,
            };
            Context.Request.Method = HttpMethods.Post;
            Context.Request.ContentType = "application/json";
            Context.Response.Body = response;
        }

        internal CountingAuthenticationService Authentication { get; } = new();

        internal CancellationTokenSource Abort { get; } = new();

        internal DefaultHttpContext Context { get; }

        /// <summary>Puts one request body on the context and answers the stream it installed.</summary>
        internal ProbeBodyStream Send(byte[] content, int maxRead, bool declared)
        {
            var stream = new ProbeBodyStream(content, maxRead);
            Context.Request.Body = stream;
            Context.Request.ContentLength = declared ? content.Length : null;
            return stream;
        }

        /// <summary>Installs a host pipe feature the handler has to hand back untouched.</summary>
        internal RecordingPipeFeature UseOriginalPipe()
        {
            var feature = new RecordingPipeFeature();
            Context.Features.Set<IRequestBodyPipeFeature>(feature);
            return feature;
        }

        internal FakeMaxRequestBodySizeFeature? UseHostLimit(HostLimit host)
        {
            if (host == HostLimit.Missing)
            {
                return null;
            }

            var feature = new FakeMaxRequestBodySizeFeature
            {
                IsReadOnly = host == HostLimit.ReadOnlySmaller,
                Stored = host switch
                {
                    HostLimit.ReadOnlySmaller or HostLimit.WritableSmaller => 1024L,
                    HostLimit.WritableLarger => 30L * 1024 * 1024,
                    _ => null,
                },
            };
            Context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
            return feature;
        }

        internal Task<IResult> RunAsync(
            ManagementLoginAdapter adapter,
            TimeSpan? budget = null) =>
            ManagementSessionHandlers.LoginAsync(Context, adapter, budget ?? Budget);

        internal async Task<LoginOutcome> LoginAsync(RecordingAdapter adapter, TimeSpan? budget = null)
        {
            var result = await RunAsync(adapter.InvokeAsync, budget);
            await result.ExecuteAsync(Context);
            return new LoginOutcome(
                Context.Response.StatusCode,
                Encoding.UTF8.GetString(response.ToArray()));
        }

        public void Dispose()
        {
            provider.Dispose();
            Abort.Dispose();
            response.Dispose();
        }
    }

    /// <summary>
    /// A request body that is readable and never seekable, answers at most a configured number of
    /// bytes per read, counts what it handed out, and can fail or wait on the token it was given.
    /// </summary>
    private sealed class ProbeBodyStream(byte[] content, int maxRead) : Stream
    {
        private int position;

        internal int BytesRead { get; private set; }

        internal bool Disposed { get; private set; }

        /// <summary>Awaited before every read, so a test can order events instead of sleeping.</summary>
        internal Func<CancellationToken, Task>? OnRead { get; set; }

        internal Exception? Failure { get; set; }

        internal int FailAfterBytes { get; set; } = -1;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfFailing();
            return Copy(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (OnRead is { } hook)
            {
                await hook(cancellationToken).ConfigureAwait(false);
            }

            ThrowIfFailing();
            cancellationToken.ThrowIfCancellationRequested();
            return Copy(buffer.Span);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private void ThrowIfFailing()
        {
            if (Failure is { } failure && FailAfterBytes >= 0 && BytesRead >= FailAfterBytes)
            {
                throw failure;
            }
        }

        private int Copy(Span<byte> destination)
        {
            var available = Math.Min(Math.Min(destination.Length, maxRead), content.Length - position);
            if (available <= 0)
            {
                return 0;
            }

            content.AsSpan(position, available).CopyTo(destination);
            position += available;
            BytesRead += available;
            return available;
        }
    }

    /// <summary>
    /// A host pipe feature whose reader records whether the login handler completed it and refuses
    /// to be read at all: the adapter must be handed the admitted copy instead.
    /// </summary>
    private sealed class RecordingPipeFeature : IRequestBodyPipeFeature
    {
        private readonly RecordingPipeReader reader = new();

        public PipeReader Reader => reader;

        internal bool Completed => reader.Completed;

        private sealed class RecordingPipeReader : PipeReader
        {
            internal bool Completed { get; private set; }

            public override void AdvanceTo(SequencePosition consumed)
            {
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
            }

            public override void CancelPendingRead()
            {
            }

            public override void Complete(Exception? exception = null) => Completed = true;

            public override ValueTask<ReadResult> ReadAsync(
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("The host body reader was read after admission.");

            public override bool TryRead(out ReadResult result) =>
                throw new InvalidOperationException("The host body reader was read after admission.");
        }
    }

    /// <summary>A writable or read-only host request-size feature.</summary>
    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        internal long? Stored { get; set; }

        public bool IsReadOnly { get; set; }

        public long? MaxRequestBodySize
        {
            get => Stored;
            set
            {
                if (IsReadOnly)
                {
                    throw new InvalidOperationException("The request size limit is read-only.");
                }

                Stored = value;
            }
        }
    }

    /// <summary>
    /// Stands in for a consuming service's login adapter, reading the body the way the test asked
    /// for and remembering the exact bytes it saw.
    /// </summary>
    private sealed class RecordingAdapter
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal List<byte[]> Bodies { get; } = [];

        internal ReadStyle Style { get; set; } = ReadStyle.CopyToAsync;

        internal Exception? Failure { get; set; }

        internal Stream? SeenBody { get; private set; }

        /// <summary>
        /// An optional asynchronous checkpoint after this adapter has captured the installed body
        /// and before it starts reading. The hook receives the login token so a stalled test can be
        /// cancelled without leaving an adapter waiting forever.
        /// </summary>
        internal Func<CancellationToken, Task>? OnEntered { get; set; }

        internal async ValueTask<ManagementIdentityResult> InvokeAsync(
            HttpContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            SeenBody = context.Request.Body;
            if (OnEntered is { } onEntered)
            {
                await onEntered(cancellationToken).ConfigureAwait(false);
            }

            var body = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
            lock (Bodies)
            {
                Bodies.Add(body);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return ManagementIdentityResult.Authenticated(Identity());
        }

        private async ValueTask<byte[]> ReadAsync(HttpContext context, CancellationToken token)
        {
            var body = context.Request.Body;
            using var buffer = new MemoryStream();
            int read;
            switch (Style)
            {
                case ReadStyle.None:
                    break;
                case ReadStyle.Prefix:
                    var prefix = new byte[16];
                    read = await body.ReadAsync(prefix, token).ConfigureAwait(false);
                    buffer.Write(prefix, 0, read);
                    break;
                case ReadStyle.SyncRead:
                    var synchronous = new byte[7];
                    while ((read = body.Read(synchronous, 0, synchronous.Length)) > 0)
                    {
                        buffer.Write(synchronous, 0, read);
                    }

                    break;
                case ReadStyle.ReadByte:
                    int single;
                    while ((single = body.ReadByte()) >= 0)
                    {
                        buffer.WriteByte((byte)single);
                    }

                    break;
                case ReadStyle.ReadAsyncMemory:
                    var memory = new byte[13];
                    while ((read = await body.ReadAsync(memory, token).ConfigureAwait(false)) > 0)
                    {
                        buffer.Write(memory, 0, read);
                    }

                    break;
                case ReadStyle.ReadAsyncArray:
                    var array = new byte[11];
#pragma warning disable CA1835
                    while ((read = await body
                        .ReadAsync(array, 0, array.Length, token)
                        .ConfigureAwait(false)) > 0)
#pragma warning restore CA1835
                    {
                        buffer.Write(array, 0, read);
                    }

                    break;
                case ReadStyle.CopyToAsync:
                    await body.CopyToAsync(buffer, token).ConfigureAwait(false);
                    break;
                case ReadStyle.StreamReader:
                    using (var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true))
                    {
                        var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                        var bytes = Encoding.UTF8.GetBytes(text);
                        buffer.Write(bytes, 0, bytes.Length);
                    }

                    break;
                default:
                    // The reader is obtained inside the adapter, which is the only place a consumer
                    // can ask for one.
                    var pipe = context.Request.BodyReader;
                    while (true)
                    {
                        var result = await pipe.ReadAsync(token).ConfigureAwait(false);
                        foreach (var segment in result.Buffer)
                        {
                            buffer.Write(segment.Span);
                        }

                        pipe.AdvanceTo(result.Buffer.End);
                        if (result.IsCompleted)
                        {
                            break;
                        }
                    }

                    break;
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Holds every participating adapter after it captures its admitted body until the test has
    /// observed that all bodies are live together.
    /// </summary>
    private sealed class AdapterRendezvous(int participantCount)
    {
        private readonly TaskCompletionSource allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;

        internal Task AllEntered => allEntered.Task;

        internal async Task EnterAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref entered);
            if (count > participantCount)
            {
                throw new InvalidOperationException("Too many adapters entered the rendezvous.");
            }

            if (count == participantCount)
            {
                allEntered.TrySetResult();
            }

            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void Release() => released.TrySetResult();
    }

    /// <summary>Counts the sign-ins a login attempted.</summary>
    private sealed class CountingAuthenticationService : IAuthenticationService
    {
        internal int SignInCalls { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignInCalls++;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }

    /// <summary>
    /// A loopback Kestrel host for the one claim a fake context cannot make: a real HTTP/1.1 request
    /// whose body has no declared length at all. The request is framed by this fixture, so the
    /// assertions are about the bytes that actually went out.
    /// </summary>
    private sealed class ChunkedLoginHost : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly int port;
        private readonly string loginPath;

        private ChunkedLoginHost(
            WebApplication application,
            RecordingAdapter adapter,
            int port,
            string loginPath)
        {
            this.application = application;
            this.port = port;
            this.loginPath = loginPath;
            Adapter = adapter;
        }

        internal RecordingAdapter Adapter { get; }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        internal static async Task<ChunkedLoginHost> StartAsync()
        {
            var adapter = new RecordingAdapter();
            var builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var mantle = builder.Services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.0");
            mantle.AddSensitiveHeaders();
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting(options =>
            {
                options.Setup.PermitLimit = 60;
                options.Management.PermitLimit = 120;
            });
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1();
            mantle.AddServiceMantleManagementEntries();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new FixedHealthSource());

            var application = builder.Build();
            try
            {
                application.UseServiceMantlePipeline();
                application.MapServiceMantleManagementSession(adapter.InvokeAsync);
                await application.StartAsync(Token);
                var address = application.Services
                    .GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses
                    .First();
                return new ChunkedLoginHost(
                    application,
                    adapter,
                    new Uri(address).Port,
                    ManagementApiDefaults.DefaultRootPath +
                        ManagementEntryDefaults.SessionLoginPath);
            }
            catch (Exception)
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Sends one HTTP/1.1 request whose body is chunked and carries no length header, and
        /// answers both the exact request text and the response.
        /// </summary>
        internal async Task<ChunkedExchange> SendChunkedAsync(int bodyLength, int chunkSize)
        {
            var head = "POST " + loginPath + " HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Content-Type: application/json\r\n" +
                ManagementEntryDefaults.UnsafeRequestHeaderName + ": " +
                ManagementEntryDefaults.UnsafeRequestHeaderValue + "\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "Connection: close\r\n\r\n";
            var body = Body(bodyLength);
            using var request = new MemoryStream();
            request.Write(Encoding.ASCII.GetBytes(head));
            for (var offset = 0; offset < body.Length; offset += chunkSize)
            {
                var size = Math.Min(chunkSize, body.Length - offset);
                var header = size.ToString("x", CultureInfo.InvariantCulture) + "\r\n";
                request.Write(Encoding.ASCII.GetBytes(header));
                request.Write(body, offset, size);
                request.Write("\r\n"u8);
            }

            request.Write("0\r\n\r\n"u8);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, Token);
            await using var stream = client.GetStream();
            var payload = request.ToArray();
            var send = Task.Run(
                async () =>
                {
                    try
                    {
                        await stream.WriteAsync(payload, Token);
                        await stream.FlushAsync(Token);
                    }
                    catch (Exception exception)
                        when (exception is IOException or ObjectDisposedException or SocketException)
                    {
                        // A server that stopped reading the oversized body closes the connection;
                        // the response it already wrote is what this exchange is about.
                    }
                },
                Token);

            using var received = new MemoryStream();
            try
            {
                await stream.CopyToAsync(received, Token);
            }
            catch (IOException)
            {
                // Same as above: whatever arrived before the close is the response.
            }

            await send;
            return new ChunkedExchange(head, Encoding.ASCII.GetString(received.ToArray()));
        }

        public async ValueTask DisposeAsync() => await application.DisposeAsync();

        private sealed class FixedHealthSource : IServiceHealthSnapshotSource
        {
            public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(new ServiceHealthSnapshot(
                    ServiceStartupPhase.Completed,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Reachable));
        }
    }
}

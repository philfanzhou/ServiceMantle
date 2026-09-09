using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Covers what the Bootstrap management entries accept on the wire, and which answer wins when the
/// request is aborted at the same moment as an internal outcome.
/// </summary>
public sealed class ServiceMantleBootstrapRequestTests
{
    private const string InvalidRequest = "management.request.invalid";
    private const string ValidBody =
        """{"database":{"provider":"PostgreSQL","connectionString":"Host=db;Password=p"},"masterKey":"k"}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> RejectedBodies => new()
    {
        { "empty", "" },
        { "not-json", "not json" },
        { "array", """[{"masterKey":"k"}]""" },
        { "string-root", "\"masterKey\"" },
        { "null-root", "null" },
        { "extra-top-level-value", ValidBody + " {}" },
        { "comment", """{"masterKey":"k" /* comment */, "database":{"provider":"P","connectionString":"c"}}""" },
        { "trailing-comma", """{"database":{"provider":"P","connectionString":"c"},"masterKey":"k",}""" },
        { "unknown-property", """{"database":{"provider":"P","connectionString":"c"},"masterKey":"k","extra":1}""" },
        { "disk-property-service-id", """{"ServiceId":"catalog","database":{"provider":"P","connectionString":"c"},"masterKey":"k"}""" },
        { "disk-property-format-version", """{"formatVersion":1,"database":{"provider":"P","connectionString":"c"},"masterKey":"k"}""" },
        { "wrong-case-master-key", """{"database":{"provider":"P","connectionString":"c"},"MasterKey":"k"}""" },
        { "wrong-case-database", """{"Database":{"provider":"P","connectionString":"c"},"masterKey":"k"}""" },
        { "duplicate-master-key", """{"database":{"provider":"P","connectionString":"c"},"masterKey":"a","masterKey":"b"}""" },
        { "escaped-duplicate-master-key", """{"database":{"provider":"P","connectionString":"c"},"masterKey":"a","masterKey":"b"}""" },
        { "missing-database", """{"masterKey":"k"}""" },
        { "missing-master-key", """{"database":{"provider":"P","connectionString":"c"}}""" },
        { "null-database", """{"database":null,"masterKey":"k"}""" },
        { "null-master-key", """{"database":{"provider":"P","connectionString":"c"},"masterKey":null}""" },
        { "blank-master-key", """{"database":{"provider":"P","connectionString":"c"},"masterKey":"   "}""" },
        { "numeric-master-key", """{"database":{"provider":"P","connectionString":"c"},"masterKey":1}""" },
        { "database-not-object", """{"database":"P","masterKey":"k"}""" },
        { "database-missing-provider", """{"database":{"connectionString":"c"},"masterKey":"k"}""" },
        { "database-missing-connection", """{"database":{"provider":"P"},"masterKey":"k"}""" },
        { "database-null-provider", """{"database":{"provider":null,"connectionString":"c"},"masterKey":"k"}""" },
        { "database-blank-connection", """{"database":{"provider":"P","connectionString":"  "},"masterKey":"k"}""" },
        { "database-unknown-property", """{"database":{"provider":"P","connectionString":"c","path":"x"},"masterKey":"k"}""" },
        { "database-duplicate-provider", """{"database":{"provider":"P","provider":"Q","connectionString":"c"},"masterKey":"k"}""" },
        { "database-blank-server-version", """{"database":{"provider":"P","connectionString":"c","serverVersion":"  "},"masterKey":"k"}""" },
        { "database-numeric-server-version", """{"database":{"provider":"P","connectionString":"c","serverVersion":15},"masterKey":"k"}""" },
        { "provider-leading-symbol", """{"database":{"provider":"-bad","connectionString":"c"},"masterKey":"k"}""" },
        { "provider-illegal-character", """{"database":{"provider":"bad provider","connectionString":"c"},"masterKey":"k"}""" },
        { "provider-too-long", """{"database":{"provider":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","connectionString":"c"},"masterKey":"k"}""" },
        { "too-deep", """{"database":{"provider":"P","connectionString":"c"},"masterKey":{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}}""" },
    };

    [Theory]
    [MemberData(nameof(RejectedBodies))]
    public async Task An_unusable_creation_body_is_the_fixed_rejection(string scenario, string body)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var response = await fixture.PostAsync(credential, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequest, await ErrorCodeAsync(response));
        Assert.Equal(0, fixture.Validator.Calls);
        Assert.False(File.Exists(fixture.BootstrapPath));
        // The rejection came before anything was consumed, so the credential is still usable.
        using var accepted = await fixture.PostAsync(credential);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.NotEmpty(scenario);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; boundary=x")]
    [InlineData("application/xml")]
    public async Task An_unusable_media_type_is_the_fixed_rejection(string? contentType)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var response = await fixture.SendAsync(
            HttpMethod.Post,
            ValidBody,
            contentType: contentType,
            credential: [credential]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequest, await ErrorCodeAsync(response));
        Assert.Equal(0, fixture.Validator.Calls);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    public async Task The_accepted_media_types_reach_the_handler(string contentType)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var response = await fixture.SendAsync(
            HttpMethod.Post,
            ValidBody,
            contentType: contentType,
            credential: [credential]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_query_string_or_a_content_encoding_is_the_fixed_rejection()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var query = await fixture.SendAsync(
            HttpMethod.Post,
            ValidBody,
            credential: [credential],
            path: fixture.EntryPath + "?force=1");
        using var encoded = await fixture.SendAsync(
            HttpMethod.Post,
            ValidBody,
            credential: [credential],
            contentEncoding: "gzip");

        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, encoded.StatusCode);
        Assert.Equal(0, fixture.Validator.Calls);
    }

    [Fact]
    public async Task An_invalid_utf8_body_and_a_byte_order_mark_are_the_fixed_rejection()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        var valid = Encoding.UTF8.GetBytes(ValidBody);

        using var bom = await SendBytesAsync(fixture, [0xEF, 0xBB, 0xBF, .. valid], credential);
        using var broken = await SendBytesAsync(
            fixture,
            [.. valid[..^1], 0xFF, (byte)'}'],
            credential);

        Assert.Equal(HttpStatusCode.BadRequest, bom.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal(0, fixture.Validator.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task The_body_limit_is_the_same_with_and_without_a_declared_length(
        bool atLimit,
        bool declaredLength)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();
        var body = BuildSizedBody(atLimit ? 65536 : 65537);
        Assert.Equal(atLimit ? 65536 : 65537, body.Length);

        using var response = await SendBytesAsync(fixture, body, credential, declaredLength);

        Assert.Equal(
            atLimit ? HttpStatusCode.Created : HttpStatusCode.BadRequest,
            response.StatusCode);
        Assert.Equal(atLimit ? 1 : 0, fixture.Validator.Calls);
    }

    [Fact]
    public async Task An_update_retains_what_it_did_not_send_and_replaces_what_it_did()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        using var created = await fixture.PostAsync(
            await fixture.ProvisionAsync(),
            """{"database":{"provider":"PostgreSQL","connectionString":"Host=original","serverVersion":"15"},"masterKey":"original-key"}""");
        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        var cookie = fixture.Cookie(ManagementPermission.Admin);

        using var masterKeyOnly = await fixture.PutAsync(cookie, """{"masterKey":"next-key"}""");
        var afterMasterKey = Load(fixture);
        using var databaseOnly = await fixture.PutAsync(
            cookie,
            """{"database":{"provider":"PostgreSQL","connectionString":"Host=replaced"}}""");
        var afterDatabase = Load(fixture);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, masterKeyOnly.StatusCode);
        Assert.Equal("Host=original", afterMasterKey.Database.ConnectionString);
        Assert.Equal("15", afterMasterKey.Database.ServerVersion);
        Assert.Equal("next-key", afterMasterKey.MasterKey);
        Assert.Equal(HttpStatusCode.OK, databaseOnly.StatusCode);
        // A supplied database is a complete replacement, never a merge: the server version it did
        // not carry is gone.
        Assert.Equal("Host=replaced", afterDatabase.Database.ConnectionString);
        Assert.Null(afterDatabase.Database.ServerVersion);
        Assert.Equal("next-key", afterDatabase.MasterKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"database":null}""")]
    [InlineData("""{"masterKey":null}""")]
    [InlineData("""{"masterKey":"   "}""")]
    [InlineData("""{"database":{"provider":"P"}}""")]
    public async Task An_unusable_update_body_is_the_fixed_rejection(string body)
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        using var created = await fixture.PostAsync(await fixture.ProvisionAsync());
        fixture.Snapshot.Current = BootstrapManagementHostFixture.Ready;
        var before = await File.ReadAllBytesAsync(fixture.BootstrapPath, Token);
        var calls = fixture.Validator.Calls;

        using var response = await fixture.PutAsync(
            fixture.Cookie(ManagementPermission.Admin),
            body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequest, await ErrorCodeAsync(response));
        Assert.Equal(calls, fixture.Validator.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.BootstrapPath, Token));
    }

    [Fact]
    public async Task An_omitted_server_version_and_an_explicit_null_both_mean_absent()
    {
        await using var fixture = await BootstrapManagementHostFixture.StartAsync();
        var credential = await fixture.ProvisionAsync();

        using var response = await fixture.PostAsync(
            credential,
            """{"database":{"provider":"PostgreSQL","connectionString":"Host=db","serverVersion":null},"masterKey":"k"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(Load(fixture).Database.ServerVersion);
    }

    [Theory]
    [InlineData("consume-returns")]
    [InlineData("consume-throws")]
    [InlineData("parse")]
    [InlineData("validate")]
    public async Task Caller_cancellation_outranks_every_boundary_outcome(string boundary)
    {
        using var directory = TemporaryBootstrapDirectory.Create();
        using var abort = new CancellationTokenSource();
        var store = new CancellingCredentialStore(boundary, abort);
        var latch = new ServiceMantleBootstrapRestartLatch();
        var manager = directory.CreateManager(_ =>
        {
            if (boundary == "validate")
            {
                abort.Cancel();
            }

            return ValueTask.FromResult(BootstrapValidationResult.Success());
        });
        var context = directory.CreateContext(store, manager, latch, abort.Token, boundary == "parse"
            ? new CancellingBody(abort)
            : new MemoryStream(Encoding.UTF8.GetBytes(ValidBody)));
        context.Request.Headers["X-ServiceMantle-Bootstrap-Credential"] =
            BootstrapCredential.Generate().Reveal();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ServiceMantleBootstrapHandlers.CreateAsync(context));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(failure.InnerException);
        // A cancellation observed before the manager published leaves no file and no latch.
        Assert.False(latch.RestartRequired);
        Assert.False(File.Exists(directory.BootstrapPath));
    }

    /// <summary>
    /// The latch is set as soon as the manager confirms the publication, so a request aborted after
    /// that point still reports a changed file rather than pretending nothing happened.
    /// </summary>
    [Fact]
    public async Task A_cancellation_after_the_publication_keeps_the_file_and_the_latch()
    {
        using var directory = TemporaryBootstrapDirectory.Create();
        using var abort = new CancellationTokenSource();
        var latch = new ServiceMantleBootstrapRestartLatch();
        var manager = directory.CreateManager(
            _ => ValueTask.FromResult(BootstrapValidationResult.Success()));
        // The abort is raised exactly when the handler reaches for the latch, which happens only
        // after the manager returned.
        var context = directory.CreateContext(
            new AlwaysConsumingCredentialStore(),
            manager,
            latch,
            abort.Token,
            new MemoryStream(Encoding.UTF8.GetBytes(ValidBody)),
            onLatchResolved: abort.Cancel);
        context.Request.Headers["X-ServiceMantle-Bootstrap-Credential"] =
            BootstrapCredential.Generate().Reveal();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ServiceMantleBootstrapHandlers.CreateAsync(context));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.True(latch.RestartRequired);
        Assert.True(File.Exists(directory.BootstrapPath));
    }

    [Fact]
    public async Task Without_caller_cancellation_an_internal_cancellation_is_still_unavailable()
    {
        using var directory = TemporaryBootstrapDirectory.Create();
        var latch = new ServiceMantleBootstrapRestartLatch();
        var manager = directory.CreateManager(_ =>
            throw new OperationCanceledException("internal", new CancellationTokenSource().Token));
        var store = new AlwaysConsumingCredentialStore();
        var context = directory.CreateContext(
            store,
            manager,
            latch,
            CancellationToken.None,
            new MemoryStream(Encoding.UTF8.GetBytes(ValidBody)));
        context.Request.Headers["X-ServiceMantle-Bootstrap-Credential"] =
            BootstrapCredential.Generate().Reveal();

        var result = await ServiceMantleBootstrapHandlers.CreateAsync(context);

        Assert.Same(ServiceMantleBootstrapResult.Unavailable, result);
        Assert.False(latch.RestartRequired);
        Assert.False(File.Exists(directory.BootstrapPath));
    }

    [Fact]
    public async Task A_response_that_already_started_is_left_alone()
    {
        var context = new DefaultHttpContext();
        var body = new StartedResponseBody();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Features.Set<IHttpResponseBodyFeature>(body);

        await ServiceMantleBootstrapResult.Created.ExecuteAsync(context);
        await ServiceMantleBootstrapResult.Unavailable.ExecuteAsync(context);

        Assert.Equal(0, body.Written);
    }

    private static BootstrapConfiguration Load(BootstrapManagementHostFixture fixture) =>
        new BootstrapFileStore(
            ServiceId.Parse("catalog"),
            new BootstrapDatabaseProviderRegistry([]),
            fixture.BootstrapPath).Load();

    private static byte[] BuildSizedBody(int totalBytes)
    {
        const string Prefix =
            "{\"database\":{\"provider\":\"PostgreSQL\",\"connectionString\":\"Host=db;Padding=";
        const string Suffix = "\"},\"masterKey\":\"k\"}";
        var padding = totalBytes - Prefix.Length - Suffix.Length;
        return Encoding.UTF8.GetBytes(Prefix + new string('x', padding) + Suffix);
    }

    private static Task<HttpResponseMessage> SendBytesAsync(
        BootstrapManagementHostFixture fixture,
        byte[] body,
        string credential,
        bool declaredLength = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, fixture.EntryPath);
        request.Headers.TryAddWithoutValidation(
            ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderName,
            ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderValue);
        request.Headers.TryAddWithoutValidation(
            "X-ServiceMantle-Bootstrap-Credential",
            credential);
        HttpContent content = declaredLength
            ? new ByteArrayContent(body)
            : new UnknownLengthContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Content = content;
        return fixture.Client.SendAsync(request, Token);
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

    /// <summary>A body whose length the request never declares, so the limit is proved by reading.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Cancels the caller during the body read, then returns a usable body.</summary>
    private sealed class CancellingBody(CancellationTokenSource abort) : Stream
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
            var bytes = Encoding.UTF8.GetBytes(ValidBody);
            bytes.CopyTo(buffer.Span);
            return ValueTask.FromResult(bytes.Length);
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
    }

    private sealed class CancellingCredentialStore(string boundary, CancellationTokenSource abort)
        : IBootstrapCredentialStore
    {
        public ValueTask<BootstrapCredentialProvisionResult> ProvisionAsync(
            BootstrapCredentialLifetime lifetime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BootstrapCredentialStatusResult> GetStatusAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BootstrapCredentialConsumptionResult> ConsumeAsync(
            string? candidate,
            CancellationToken cancellationToken = default)
        {
            if (boundary == "consume-returns")
            {
                abort.Cancel();
                return ValueTask.FromResult(BootstrapCredentialConsumptionResult.Consumed());
            }

            if (boundary == "consume-throws")
            {
                abort.Cancel();
                return ValueTask.FromException<BootstrapCredentialConsumptionResult>(
                    new InvalidOperationException("internal"));
            }

            return ValueTask.FromResult(BootstrapCredentialConsumptionResult.Consumed());
        }
    }

    private sealed class AlwaysConsumingCredentialStore : IBootstrapCredentialStore
    {
        public ValueTask<BootstrapCredentialProvisionResult> ProvisionAsync(
            BootstrapCredentialLifetime lifetime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BootstrapCredentialStatusResult> GetStatusAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BootstrapCredentialConsumptionResult> ConsumeAsync(
            string? candidate,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BootstrapCredentialConsumptionResult.Consumed());
    }

    private sealed class DelegatingValidator(
        Func<BootstrapConfiguration, ValueTask<BootstrapValidationResult>> validate)
        : IBootstrapCandidateValidator
    {
        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken) => validate(candidate);
    }

    private sealed class TemporaryBootstrapDirectory : IDisposable
    {
        private TemporaryBootstrapDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        internal string Path { get; }

        internal string BootstrapPath =>
            System.IO.Path.Combine(Path, "catalog.bootstrap.json");

        internal static TemporaryBootstrapDirectory Create() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"sm-bootstrap-handler-{Guid.NewGuid():N}"));

        internal BootstrapConfigurationManager CreateManager(
            Func<BootstrapConfiguration, ValueTask<BootstrapValidationResult>> validate) =>
            new(
                new BootstrapFileStore(
                    ServiceId.Parse("catalog"),
                    new BootstrapDatabaseProviderRegistry([]),
                    BootstrapPath),
                InstanceId.Parse("catalog-01"),
                new DelegatingValidator(validate));

        internal DefaultHttpContext CreateContext(
            IBootstrapCredentialStore store,
            BootstrapConfigurationManager manager,
            ServiceMantleBootstrapRestartLatch latch,
            CancellationToken requestAborted,
            Stream body,
            Action? onLatchResolved = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddSingleton(manager);
            services.AddSingleton(latch);
            var context = new DefaultHttpContext
            {
                RequestServices = new HookedServiceProvider(
                    services.BuildServiceProvider(),
                    onLatchResolved),
                RequestAborted = requestAborted,
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
            return context;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>Runs one hook when the restart latch is resolved, and nothing else.</summary>
    private sealed class HookedServiceProvider(IServiceProvider inner, Action? onLatchResolved)
        : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ServiceMantleBootstrapRestartLatch))
            {
                onLatchResolved?.Invoke();
            }

            return inner.GetService(serviceType);
        }
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    private sealed class StartedResponseBody : IHttpResponseBodyFeature
    {
        private readonly CountingStream stream = new();

        public Stream Stream => stream;

        public PipeWriter Writer => PipeWriter.Create(stream);

        internal int Written => stream.Written;

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(
            string path,
            long offset,
            long? count,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class CountingStream : Stream
        {
            private int written;

            internal int Written => written;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => written;

            public override long Position
            {
                get => written;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => written += count;
        }
    }
}

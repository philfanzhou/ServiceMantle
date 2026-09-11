using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Management;
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
/// Pins that a management login which fails inside <c>SignInAsync</c> leaves no part of that
/// sign-in's ticket on the response, and that the unrelated cookies the response already carried
/// survive the rollback unchanged.
/// </summary>
/// <remarks>
/// The rollback is exercised twice over: against an injected authentication service that appends a
/// complete ticket, appends chunked ticket parts, or replaces the whole header before it throws,
/// and against the real <c>CookieAuthenticationHandler</c>, whose <c>SignedIn</c> callback runs
/// after the cookie has already been appended. This file owns its own scope, host fixture, and
/// helpers so the existing session fixtures and tests keep describing what they already describe.
/// </remarks>
public sealed class ManagementSessionCookieRollbackTests
{
    /// <summary>Values a failed sign-in must never leave behind on the response.</summary>
    private const string Sentinel = "sentinel-management-ticket-value";

    private static readonly TimeSpan Budget =
        ManagementSessionOptions.MaximumLoginTimeout;

    /// <summary>The shapes a sign-in can leave a ticket in before it fails.</summary>
    public enum TicketShape
    {
        /// <summary>One complete ticket cookie.</summary>
        Complete,

        /// <summary>A chunk marker plus the two chunk cookies a chunking manager writes.</summary>
        Chunked,

        /// <summary>A manager that rewrote the whole header instead of appending to it.</summary>
        Replacement
    }

    /// <summary>The finite set of ways a sign-in can leave after it wrote a ticket.</summary>
    public enum FailureKind
    {
        OrdinaryException,
        InternalCancellation
    }

    /// <summary>The response states a login can start a sign-in from.</summary>
    public enum ExistingCookies
    {
        None,
        OneUnrelated,
        SeveralUnrelated,
        ExistingManagement
    }

    public static TheoryData<TicketShape, FailureKind> TicketsAndFailures =>
        new()
        {
            { TicketShape.Complete, FailureKind.OrdinaryException },
            { TicketShape.Complete, FailureKind.InternalCancellation },
            { TicketShape.Chunked, FailureKind.OrdinaryException },
            { TicketShape.Chunked, FailureKind.InternalCancellation },
            { TicketShape.Replacement, FailureKind.OrdinaryException },
            { TicketShape.Replacement, FailureKind.InternalCancellation },
        };

    public static TheoryData<ExistingCookies, TicketShape> SnapshotMatrix
    {
        get
        {
            var matrix = new TheoryData<ExistingCookies, TicketShape>();
            foreach (var existing in Enum.GetValues<ExistingCookies>())
            {
                foreach (var shape in Enum.GetValues<TicketShape>())
                {
                    matrix.Add(existing, shape);
                }
            }

            return matrix;
        }
    }

    [Theory]
    [MemberData(nameof(TicketsAndFailures))]
    public async Task A_failed_sign_in_answers_the_fixed_503_without_this_ticket(
        TicketShape shape,
        FailureKind failure)
    {
        using var scope = new RollbackScope(ExistingCookies.SeveralUnrelated);
        scope.Authentication.OnSignIn = context => WriteTicket(context.Response, shape);
        scope.Authentication.SignInFailure = () => Failure(failure);

        var result = await LoginAsync(scope);
        await result.ExecuteAsync(scope.Context);

        Assert.Same(ManagementSessionResult.Unavailable, result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, scope.Context.Response.StatusCode);
        Assert.Equal(1, scope.Authentication.SignInCalls);
        // The failed ticket is gone in every shape it was written in, and nothing was compensated
        // with a sign-out.
        Assert.Equal(scope.Existing, scope.SetCookie);
        Assert.DoesNotContain(Sentinel, string.Join('\n', scope.SetCookie), StringComparison.Ordinal);
        Assert.Equal(0, scope.Authentication.SignOutCalls);
        Assert.Equal(0, scope.Lifetime.Aborts);
    }

    [Theory]
    [MemberData(nameof(SnapshotMatrix))]
    public async Task The_snapshot_restores_the_original_cookies_value_by_value(
        ExistingCookies existing,
        TicketShape shape)
    {
        using var scope = new RollbackScope(existing);
        scope.Authentication.OnSignIn = context => WriteTicket(context.Response, shape);
        scope.Authentication.SignInFailure = () => Failure(FailureKind.OrdinaryException);

        var result = await LoginAsync(scope);

        Assert.Same(ManagementSessionResult.Unavailable, result);
        var restored = scope.SetCookie;
        Assert.Equal(scope.Existing.Length, restored.Length);
        for (var index = 0; index < restored.Length; index++)
        {
            // Content and position both matter: an unrelated cookie must come back as itself, in
            // the place it had, and a pre-existing management cookie is not this login's ticket.
            Assert.Equal(scope.Existing[index], restored[index]);
        }

        Assert.Equal(0, scope.Authentication.SignOutCalls);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_its_own_token_only_after_the_ticket_is_rolled_back()
    {
        using var scope = new RollbackScope(ExistingCookies.OneUnrelated);
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            await scope.Abort.CancelAsync();
        };
        scope.Authentication.SignInFailure = () => new InvalidOperationException(Sentinel);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => LoginAsync(scope));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Sentinel, exception.ToString(), StringComparison.Ordinal);
        // The rollback is the shared exit and runs before the caller's cancellation is answered.
        Assert.Equal(scope.Existing, scope.SetCookie);
    }

    [Fact]
    public async Task A_response_that_already_started_is_left_exactly_as_it_was_sent()
    {
        using var scope = new RollbackScope(ExistingCookies.OneUnrelated);
        string[] sent = [];
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            scope.Response.HasStarted = true;
            sent = scope.SetCookie;
        };
        scope.Authentication.SignInFailure = () => new InvalidOperationException(Sentinel);

        var result = await LoginAsync(scope);
        await result.ExecuteAsync(scope.Context);

        Assert.Same(ManagementSessionResult.Unavailable, result);
        // A sent response is a declared non-guarantee: its headers are not rewritten, no second
        // body is attempted, and the connection is not torn down behind the caller's back.
        Assert.Equal(sent, scope.SetCookie);
        Assert.Contains(Sentinel, string.Join('\n', scope.SetCookie), StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status200OK, scope.Context.Response.StatusCode);
        Assert.Equal(0, scope.Body.Writes);
        Assert.Equal(0, scope.Lifetime.Aborts);
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_read_signs_nothing_in()
    {
        using var scope = new RollbackScope(ExistingCookies.OneUnrelated);
        scope.Headers.Mode = HeaderMode.Unreadable;

        var result = await LoginAsync(scope);

        // Without a snapshot there would be nothing to roll back to, so the sign-in never starts.
        Assert.Same(ManagementSessionResult.Unavailable, result);
        Assert.Equal(0, scope.Authentication.SignInCalls);
        Assert.Equal(0, scope.Lifetime.Aborts);
    }

    [Theory]
    [InlineData(HeaderMode.SilentlyDropsWrites)]
    [InlineData(HeaderMode.ThrowsOnWrite)]
    public async Task A_rollback_that_cannot_be_applied_aborts_instead_of_answering(HeaderMode mode)
    {
        using var scope = new RollbackScope(ExistingCookies.OneUnrelated);
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            scope.Headers.Mode = mode;
        };
        scope.Authentication.SignInFailure = () => new InvalidOperationException(Sentinel);

        var result = await LoginAsync(scope);
        await result.ExecuteAsync(scope.Context);

        // The response still holds the failed ticket, so it is aborted rather than completed: no
        // status code, no header, and no body of the fixed result reach the caller.
        Assert.Same(ManagementSessionResult.Terminated, result);
        Assert.Equal(1, scope.Lifetime.Aborts);
        Assert.Equal(StatusCodes.Status200OK, scope.Context.Response.StatusCode);
        Assert.Equal(0, scope.Body.Writes);
    }

    [Fact]
    public async Task Two_concurrent_logins_never_restore_each_other_s_cookies()
    {
        using var succeeding = new RollbackScope(ExistingCookies.OneUnrelated);
        using var failing = new RollbackScope(ExistingCookies.SeveralUnrelated);
        var succeedingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        succeeding.Authentication.OnSignIn = async context =>
        {
            Append(context.Response, ManagementSessionDefaults.CookieName + "=granted; path=/");
            succeedingEntered.TrySetResult();
            await failingEntered.Task;
        };
        failing.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            failingEntered.TrySetResult();
            await succeedingEntered.Task;
        };
        failing.Authentication.SignInFailure = () => new InvalidOperationException(Sentinel);

        var results = await Task.WhenAll(LoginAsync(succeeding), LoginAsync(failing));

        Assert.Same(ManagementSessionResult.NoContent, results[0]);
        Assert.Same(ManagementSessionResult.Unavailable, results[1]);
        string[] expected =
            [.. succeeding.Existing, ManagementSessionDefaults.CookieName + "=granted; path=/"];
        Assert.Equal(expected, succeeding.SetCookie);
        Assert.Equal(failing.Existing, failing.SetCookie);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_real_cookie_handler_sends_no_ticket_when_its_signed_in_callback_throws(
        bool chunked)
    {
        await using var host = await RollbackHostFixture.StartAsync(failSignIn: true, chunked: chunked);

        using var response = await host.LoginAsync();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The real handler appends the cookie before it raises SignedIn, so this is the shape the
        // rollback exists for.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(Sentinel, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_renewable_existing_cookie_is_not_reissued_by_a_failed_login()
    {
        await using var host = await RollbackHostFixture.StartAsync(failSignIn: true, chunked: false);

        using var response = await host.LoginAsync(cookie: host.Cookie(TimeSpan.FromMinutes(20)));

        // The handler marks this request signed in before it throws, which suppresses the sliding
        // renewal it had scheduled; the failed login therefore issues nothing at all.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_successful_login_still_issues_a_cookie_that_authenticates()
    {
        await using var host = await RollbackHostFixture.StartAsync(failSignIn: false, chunked: false);

        using var login = await host.LoginAsync();
        var issued = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        using var read = await host.ReadSessionAsync(issued[..issued.IndexOf(';', StringComparison.Ordinal)]);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.StartsWith(
            ManagementSessionDefaults.CookieName + "=",
            issued,
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static Task<IResult> LoginAsync(RollbackScope scope) =>
        ManagementSessionHandlers.LoginAsync(
            scope.Context,
            (_, _) => ValueTask.FromResult(ManagementIdentityResult.Authenticated(Identity())),
            Budget);

    private static Task WriteTicket(HttpResponse response, TicketShape shape)
    {
        var name = ManagementSessionDefaults.CookieName;
        switch (shape)
        {
            case TicketShape.Chunked:
                Append(
                    response,
                    name + "=chunks-2; path=/; secure; httponly",
                    name + "C1=" + Sentinel + "-part-1; path=/; secure; httponly",
                    name + "C2=" + Sentinel + "-part-2; path=/; secure; httponly");
                break;
            case TicketShape.Replacement:
                // A manager that rewrites the header rather than appending to it drops the
                // unrelated cookies as well, so the snapshot is the only way back.
                response.Headers[HeaderNames.SetCookie] =
                    new StringValues(name + "=" + Sentinel + "; path=/; secure; httponly");
                break;
            default:
                Append(response, name + "=" + Sentinel + "; path=/; secure; httponly");
                break;
        }

        return Task.CompletedTask;
    }

    private static void Append(HttpResponse response, params string[] values)
    {
        foreach (var value in values)
        {
            response.Headers.Append(HeaderNames.SetCookie, value);
        }
    }

    private static Exception Failure(FailureKind kind) => kind == FailureKind.OrdinaryException
        ? new InvalidOperationException(Sentinel)
        // An internal cancellation that is not the caller's is the same fixed outcome.
        : new OperationCanceledException(Sentinel, new CancellationTokenSource().Token);

    private static string[] CookiesFor(ExistingCookies existing) => existing switch
    {
        ExistingCookies.None => [],
        ExistingCookies.OneUnrelated => ["unrelated-a=1; path=/"],
        ExistingCookies.SeveralUnrelated =>
            ["unrelated-a=1; path=/", "unrelated-b=2; path=/; httponly", "unrelated-c=3"],
        _ =>
        [
            ManagementSessionDefaults.CookieName + "=already-here; path=/; secure; httponly",
            "unrelated-a=1; path=/"
        ],
    };

    private static ManagementIdentity Identity() => ManagementIdentity.Create(
        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
        "operator-1",
        [ManagementPermission.Read, ManagementPermission.Admin],
        "sensitive-display-name");

    /// <summary>How a scripted header dictionary answers reads and writes of <c>Set-Cookie</c>.</summary>
    public enum HeaderMode
    {
        Normal,

        /// <summary>Reading the header throws, so no snapshot can be taken.</summary>
        Unreadable,

        /// <summary>Writes are accepted and discarded, which a read-back detects.</summary>
        SilentlyDropsWrites,

        /// <summary>Writes throw.</summary>
        ThrowsOnWrite
    }

    /// <summary>Owns one direct handler call's context, response, abort source, and recorded calls.</summary>
    private sealed class RollbackScope : IDisposable
    {
        private readonly ServiceProvider provider;

        internal RollbackScope(ExistingCookies existing)
        {
            Existing = CookiesFor(existing);
            Headers = new ScriptedHeaderDictionary();
            foreach (var cookie in Existing)
            {
                Headers.Append(HeaderNames.SetCookie, cookie);
            }

            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(Authentication);
            provider = services.BuildServiceProvider();

            Response = new ScriptedResponseFeature(Headers);
            Lifetime = new RecordingLifetimeFeature(Abort.Token);
            Context = new DefaultHttpContext { RequestServices = provider };
            Context.Features.Set<IHttpResponseFeature>(Response);
            Context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Body));
            Context.Features.Set<IHttpRequestLifetimeFeature>(Lifetime);
            Context.Request.Method = HttpMethods.Post;
            Context.Request.Body = Stream.Null;
        }

        internal string[] Existing { get; }

        internal ScriptedHeaderDictionary Headers { get; }

        internal ScriptedResponseFeature Response { get; }

        internal RecordingLifetimeFeature Lifetime { get; }

        internal CountingStream Body { get; } = new();

        internal ScriptedAuthenticationService Authentication { get; } = new();

        internal CancellationTokenSource Abort { get; } = new();

        internal DefaultHttpContext Context { get; }

        /// <summary>The response's current <c>Set-Cookie</c> values, read past any scripted mode.</summary>
        internal string[] SetCookie => Headers.Raw;

        public void Dispose()
        {
            provider.Dispose();
            Abort.Dispose();
            Body.Dispose();
        }
    }

    /// <summary>A response whose header dictionary and started flag the test drives directly.</summary>
    private sealed class ScriptedResponseFeature(IHeaderDictionary headers) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = headers;

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    /// <summary>Counts the aborts a handler asks for and carries the caller's request token.</summary>
    private sealed class RecordingLifetimeFeature(CancellationToken requestAborted)
        : IHttpRequestLifetimeFeature
    {
        private int aborts;

        public CancellationToken RequestAborted { get; set; } = requestAborted;

        internal int Aborts => Volatile.Read(ref aborts);

        public void Abort() => Interlocked.Increment(ref aborts);
    }

    /// <summary>Counts response body writes without keeping any of them.</summary>
    private sealed class CountingStream : Stream
    {
        private int writes;

        internal int Writes => Volatile.Read(ref writes);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Interlocked.Increment(ref writes);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A header dictionary that can be switched into refusing reads or writes of <c>Set-Cookie</c>
    /// while the test still sees what the response really holds.
    /// </summary>
    private sealed class ScriptedHeaderDictionary : IHeaderDictionary
    {
        private readonly IHeaderDictionary inner = new HeaderDictionary();

        internal HeaderMode Mode { get; set; } = HeaderMode.Normal;

        /// <summary>What the response really holds, whatever mode reads and writes are in.</summary>
        internal string[] Raw
        {
            get
            {
                var values = inner[HeaderNames.SetCookie];
                var raw = new string[values.Count];
                for (var index = 0; index < raw.Length; index++)
                {
                    raw[index] = values[index] ?? string.Empty;
                }

                return raw;
            }
        }

        public long? ContentLength
        {
            get => inner.ContentLength;
            set => inner.ContentLength = value;
        }

        public ICollection<string> Keys => inner.Keys;

        public ICollection<StringValues> Values => inner.Values;

        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        public StringValues this[string key]
        {
            get => IsSetCookie(key) && Mode == HeaderMode.Unreadable
                ? throw new InvalidOperationException(Sentinel)
                : inner[key];
            set
            {
                if (Writable(key))
                {
                    inner[key] = value;
                }
            }
        }

#pragma warning disable ASP0019 // The interface member is implemented, not used to set a header.
        public void Add(string key, StringValues value)
        {
            if (Writable(key))
            {
                inner.Add(key, value);
            }
        }

        public void Add(KeyValuePair<string, StringValues> item)
        {
            if (Writable(item.Key))
            {
                inner.Add(item);
            }
        }
#pragma warning restore ASP0019

        public void Clear() => inner.Clear();

        public bool Contains(KeyValuePair<string, StringValues> item) => inner.Contains(item);

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) =>
            inner.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => inner.GetEnumerator();

        public bool Remove(string key) => Writable(key) && inner.Remove(key);

        public bool Remove(KeyValuePair<string, StringValues> item) =>
            Writable(item.Key) && inner.Remove(item);

        public bool TryGetValue(string key, out StringValues value) => inner.TryGetValue(key, out value);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            inner.GetEnumerator();

        private static bool IsSetCookie(string key) =>
            string.Equals(key, HeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Decides what a write of this header does: apply it, throw, or accept and discard it. A
        /// discarded write is only visible in a read-back, which is what the rollback relies on.
        /// </summary>
        private bool Writable(string key)
        {
            if (!IsSetCookie(key))
            {
                return true;
            }

            return Mode == HeaderMode.ThrowsOnWrite
                ? throw new InvalidOperationException(Sentinel)
                : Mode != HeaderMode.SilentlyDropsWrites;
        }
    }

    /// <summary>Records the authentication calls a handler makes and scripts what a sign-in does.</summary>
    private sealed class ScriptedAuthenticationService : IAuthenticationService
    {
        private int signInCalls;
        private int signOutCalls;

        internal int SignInCalls => Volatile.Read(ref signInCalls);

        internal int SignOutCalls => Volatile.Read(ref signOutCalls);

        /// <summary>Runs inside the sign-in, before it decides whether to fail.</summary>
        internal Func<HttpContext, Task>? OnSignIn { get; set; }

        internal Func<Exception>? SignInFailure { get; set; }

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

        public async Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref signInCalls);
            if (OnSignIn is { } onSignIn)
            {
                await onSignIn(context);
            }

            if (SignInFailure is { } failure)
            {
                throw failure();
            }
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref signOutCalls);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Starts a host around the real management cookie handler, optionally forcing chunked cookies
    /// and making the handler's <c>SignedIn</c> callback throw after the cookie was appended.
    /// </summary>
    private sealed class RollbackHostFixture : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly HttpClient client;

        private RollbackHostFixture(WebApplication application, HttpClient client)
        {
            this.application = application;
            this.client = client;
        }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        internal static async Task<RollbackHostFixture> StartAsync(bool failSignIn, bool chunked)
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
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new ReadyHealthSource());
            builder.Services.PostConfigure<CookieAuthenticationOptions>(
                ManagementSessionDefaults.AuthenticationScheme,
                options =>
                {
                    if (chunked)
                    {
                        // A chunk size just above the cookie template forces the marker plus chunks.
                        options.CookieManager = new ChunkingCookieManager { ChunkSize = 200 };
                    }

                    if (failSignIn)
                    {
                        options.Events.OnSignedIn =
                            _ => throw new InvalidOperationException(Sentinel);
                    }
                });

            var application = builder.Build();
            try
            {
                application.UseServiceMantlePipeline();
                application.MapServiceMantleManagementSession(
                    (_, _) => ValueTask.FromResult(ManagementIdentityResult.Authenticated(Identity())));
                await application.StartAsync(Token);
            }
            catch (Exception)
            {
                await application.DisposeAsync();
                throw;
            }

            return new RollbackHostFixture(application, application.GetTestClient());
        }

        internal Task<HttpResponseMessage> LoginAsync(string? cookie = null)
        {
            var request = Create(
                HttpMethod.Post,
                ManagementApiDefaults.DefaultRootPath +
                    ManagementEntryDefaults.SessionLoginPath,
                cookie);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"password\":\"secret\"}"));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Content = content;
            return client.SendAsync(request, Token);
        }

        internal Task<HttpResponseMessage> ReadSessionAsync(string cookie) =>
            client.SendAsync(
                Create(
                    HttpMethod.Get,
                    ManagementApiDefaults.DefaultRootPath +
                        ManagementEntryDefaults.SessionPath,
                    cookie),
                Token);

        /// <summary>Protects a still valid ticket that is already inside the renewal window.</summary>
        internal string Cookie(TimeSpan age)
        {
            var scheme = ManagementSessionDefaults.AuthenticationScheme;
            var options = application.Services
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(scheme);
            var issued = DateTimeOffset.UtcNow - age;
            var ticket = new AuthenticationTicket(
                Identity().ToClaimsPrincipal(),
                new AuthenticationProperties
                {
                    IssuedUtc = issued,
                    ExpiresUtc = issued + TimeSpan.FromMinutes(30),
                },
                scheme);
            return ManagementSessionDefaults.CookieName + "=" +
                options.TicketDataFormat.Protect(ticket);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.DisposeAsync();
        }

        private static HttpRequestMessage Create(HttpMethod method, string path, string? cookie)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(
                ManagementEntryDefaults.UnsafeRequestHeaderName,
                ManagementEntryDefaults.UnsafeRequestHeaderValue);
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return request;
        }

        private sealed class ReadyHealthSource : IServiceHealthSnapshotSource
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

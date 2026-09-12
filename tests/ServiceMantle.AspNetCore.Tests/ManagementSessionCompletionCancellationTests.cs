using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Session;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Pins that the session handlers observe the request token once every controlled dependency
/// settles: a sign-in, resolver, authenticate, or sign-out that completes after the caller aborted
/// answers the cancellation instead of a normal result, and a cancelled login rolls its ticket back.
/// </summary>
/// <remarks>
/// This file owns its own authentication, resolver, response, and host fixtures so the existing
/// session fixtures and tests keep describing what they already describe.
/// </remarks>
public sealed class ManagementSessionCompletionCancellationTests
{
    private const string Sentinel = "completion-ticket-sentinel";
    private const string Canary = "completion-failure-canary";
    private static readonly TimeSpan Budget = ManagementSessionOptions.MaximumLoginTimeout;

    public enum TicketShape
    {
        Complete,
        Chunked,
        Replacement
    }

    public enum ExistingCookies
    {
        None,
        OneUnrelated,
        ExistingManagement
    }

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
    [MemberData(nameof(SnapshotMatrix))]
    public async Task A_sign_in_completing_after_cancellation_is_rolled_back_value_by_value(
        ExistingCookies existing,
        TicketShape shape)
    {
        using var scope = new CompletionScope(existing);
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, shape);
            await scope.Abort.CancelAsync();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoginAsync(scope));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        // The completed ticket never survived, the pre-existing cookies came back exactly, and no
        // sign-out was used to compensate.
        Assert.Equal(scope.Existing, scope.SetCookie);
        Assert.DoesNotContain(Sentinel, string.Join('\n', scope.SetCookie), StringComparison.Ordinal);
        Assert.Equal(1, scope.Authentication.SignInCalls);
        Assert.Equal(0, scope.Authentication.SignOutCalls);
        Assert.Equal(0, scope.Lifetime.Aborts);
        Assert.Equal(0, scope.Body.Writes);
    }

    [Fact]
    public async Task A_cancelled_sign_in_whose_restore_fails_aborts_and_keeps_the_caller_token()
    {
        using var scope = new CompletionScope(ExistingCookies.OneUnrelated);
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            scope.Headers.Mode = HeaderMode.ThrowsOnWrite;
            await scope.Abort.CancelAsync();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoginAsync(scope));

        // The restore is attempted before the cancellation is answered; a response that refuses it
        // is aborted rather than completed with the ticket still on it.
        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Equal(1, scope.Lifetime.Aborts);
        Assert.Equal(0, scope.Body.Writes);
    }

    [Fact]
    public async Task A_cancelled_sign_in_into_an_already_started_response_is_left_as_sent()
    {
        using var scope = new CompletionScope(ExistingCookies.OneUnrelated);
        string[] sent = [];
        scope.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            scope.Response.HasStarted = true;
            sent = scope.SetCookie;
            await scope.Abort.CancelAsync();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoginAsync(scope));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        // A sent response is a declared non-guarantee: no header rewrite, no second body, no abort.
        Assert.Equal(sent, scope.SetCookie);
        Assert.Contains(Sentinel, string.Join('\n', scope.SetCookie), StringComparison.Ordinal);
        Assert.Equal(0, scope.Body.Writes);
        Assert.Equal(0, scope.Lifetime.Aborts);
    }

    [Fact]
    public async Task The_real_cookie_handler_ticket_is_restored_when_signed_in_cancels_the_caller()
    {
        await using var host = await CompletionHostFixture.StartAsync(cancelOnSignedIn: true);

        await host.SendCancelledLoginAsync();

        // The capture middleware observed the response and the surfaced cancellation: the real
        // handler had already appended the ticket when its SignedIn callback cancelled the caller,
        // the completion checkpoint removed it again, and the not-yet-started response carries no
        // part of the cancelled sign-in.
        Assert.NotNull(host.ObservedCancellation);
        Assert.Equal(host.RequestToken, host.ObservedCancellation);
        Assert.Empty(host.ObservedSetCookie);
    }

    [Fact]
    public async Task The_real_cookie_handler_still_answers_204_when_nobody_cancels()
    {
        await using var host = await CompletionHostFixture.StartAsync(cancelOnSignedIn: false);

        using var response = await host.LoginAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Set-Cookie"));
    }

    public static TheoryData<string> ResolverCompletions => new()
    {
        "resolved",
        "unauthenticated",
        "claims-invalid",
        "null",
        "ordinary-exception",
        "internal-cancellation"
    };

    [Theory]
    [MemberData(nameof(ResolverCompletions))]
    public async Task A_resolver_completing_after_cancellation_is_not_delivered(string mode)
    {
        using var scope = new CompletionScope(ExistingCookies.None);
        scope.Resolver.OnResolve = () =>
        {
            scope.Abort.Cancel();
            return mode switch
            {
                "resolved" => ManagementCurrentOperatorResult.Resolved(Identity()),
                "unauthenticated" => ManagementCurrentOperatorResult.Unauthenticated(),
                "claims-invalid" => ManagementCurrentOperatorResult.ClaimsInvalid(
                    WellKnownManagementIdentityErrorCodes.PermissionInvalid),
                "ordinary-exception" => throw new InvalidOperationException(Canary),
                "internal-cancellation" => throw new OperationCanceledException(
                    Canary,
                    new CancellationTokenSource().Token),
                _ => null
            };
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementSessionHandlers.CurrentAsync(scope.Context));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        // The read stopped at the resolver: no authentication was started for an aborted request.
        Assert.Equal(0, scope.Authentication.AuthenticateCalls);
    }

    [Fact]
    public async Task An_uncancelled_resolver_failure_still_propagates_unchanged()
    {
        using var scope = new CompletionScope(ExistingCookies.None);
        var original = new InvalidOperationException(Canary);
        scope.Resolver.OnResolve = () => throw original;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagementSessionHandlers.CurrentAsync(scope.Context));

        Assert.Same(original, exception);
    }

    public static TheoryData<string> AuthenticateCompletions => new()
    {
        "valid-ticket",
        "missing-expiry",
        "no-result"
    };

    [Theory]
    [MemberData(nameof(AuthenticateCompletions))]
    public async Task An_authenticate_completing_after_cancellation_is_not_delivered(string mode)
    {
        using var scope = new CompletionScope(ExistingCookies.None);
        scope.Resolver.Result = ManagementCurrentOperatorResult.Resolved(Identity());
        scope.Authentication.AuthenticateResult = mode switch
        {
            "valid-ticket" => AuthenticateSuccess(expiresUtc: DateTimeOffset.UtcNow.AddMinutes(20)),
            "missing-expiry" => AuthenticateSuccess(expiresUtc: null),
            _ => AuthenticateResult.NoResult()
        };
        scope.Authentication.OnAuthenticate = scope.Abort.Cancel;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementSessionHandlers.CurrentAsync(scope.Context));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, scope.Authentication.AuthenticateCalls);
    }

    [Fact]
    public async Task Uncancelled_current_reads_keep_their_existing_results()
    {
        using var resolved = new CompletionScope(ExistingCookies.None);
        resolved.Resolver.Result = ManagementCurrentOperatorResult.Resolved(Identity());
        resolved.Authentication.AuthenticateResult =
            AuthenticateSuccess(DateTimeOffset.UtcNow.AddMinutes(20));
        var current = await ManagementSessionHandlers.CurrentAsync(resolved.Context);
        Assert.IsType<ManagementSessionResult>(current);

        using var forbidden = new CompletionScope(ExistingCookies.None);
        forbidden.Resolver.Result = ManagementCurrentOperatorResult.Unauthenticated();
        Assert.IsType<ForbidHttpResult>(
            await ManagementSessionHandlers.CurrentAsync(forbidden.Context));

        using var unavailable = new CompletionScope(ExistingCookies.None);
        unavailable.Resolver.Result = ManagementCurrentOperatorResult.Resolved(Identity());
        unavailable.Authentication.AuthenticateResult = AuthenticateResult.NoResult();
        Assert.Same(
            ManagementSessionResult.Unavailable,
            await ManagementSessionHandlers.CurrentAsync(unavailable.Context));
    }

    [Fact]
    public async Task A_sign_out_completing_after_cancellation_keeps_the_deletion_cookie()
    {
        using var scope = new CompletionScope(ExistingCookies.None);
        scope.Authentication.OnSignOut = async context =>
        {
            context.Response.Headers.Append(
                HeaderNames.SetCookie,
                ManagementSessionDefaults.CookieName + "=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
            await scope.Abort.CancelAsync();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementSessionHandlers.LogoutAsync(scope.Context));

        Assert.Equal(scope.Abort.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        // The deletion cookie stays: a cancelled logout must not revive the old session by rolling
        // the deletion back, and the fixed 204 is not delivered either.
        Assert.Single(scope.SetCookie);
        Assert.Contains(
            "expires=Thu, 01 Jan 1970",
            scope.SetCookie[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uncancelled_logout_still_answers_204_with_the_deletion()
    {
        using var scope = new CompletionScope(ExistingCookies.None);
        scope.Authentication.OnSignOut = context =>
        {
            context.Response.Headers.Append(
                HeaderNames.SetCookie,
                ManagementSessionDefaults.CookieName + "=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
            return Task.CompletedTask;
        };

        var result = await ManagementSessionHandlers.LogoutAsync(scope.Context);
        await result.ExecuteAsync(scope.Context);

        Assert.Same(ManagementSessionResult.NoContent, result);
        Assert.Single(scope.SetCookie);
    }

    [Fact]
    public async Task Two_concurrent_logins_only_lose_the_cancelled_one()
    {
        using var cancelled = new CompletionScope(ExistingCookies.OneUnrelated);
        using var healthy = new CompletionScope(ExistingCookies.OneUnrelated);
        var cancelledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelled.Authentication.OnSignIn = async context =>
        {
            await WriteTicket(context.Response, TicketShape.Complete);
            await cancelled.Abort.CancelAsync();
            cancelledEntered.TrySetResult();
            await healthyEntered.Task;
        };
        healthy.Authentication.OnSignIn = async context =>
        {
            context.Response.Headers.Append(
                HeaderNames.SetCookie,
                ManagementSessionDefaults.CookieName + "=granted; path=/");
            healthyEntered.TrySetResult();
            await cancelledEntered.Task;
        };

        var cancelledTask = Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoginAsync(cancelled));
        var healthyResult = await LoginAsync(healthy);
        await cancelledEntered.Task;
        await healthyEntered.Task;

        Assert.Equal(cancelled.Abort.Token, (await cancelledTask).CancellationToken);
        Assert.Equal(cancelled.Existing, cancelled.SetCookie);
        Assert.Same(ManagementSessionResult.NoContent, healthyResult);
        Assert.Equal(
            [.. healthy.Existing, ManagementSessionDefaults.CookieName + "=granted; path=/"],
            healthy.SetCookie);
    }

    private static Task<IResult> LoginAsync(CompletionScope scope) =>
        ManagementSessionHandlers.LoginAsync(
            scope.Context,
            (_, _) => ValueTask.FromResult(ManagementIdentityResult.Authenticated(Identity())),
            Budget);

    private static AuthenticateResult AuthenticateSuccess(DateTimeOffset? expiresUtc)
    {
        var properties = new AuthenticationProperties();
        if (expiresUtc is { } expiry)
        {
            properties.ExpiresUtc = expiry;
        }

        return AuthenticateResult.Success(new AuthenticationTicket(
            Identity().ToClaimsPrincipal(),
            properties,
            ManagementSessionDefaults.AuthenticationScheme));
    }

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

    private static string[] CookiesFor(ExistingCookies existing) => existing switch
    {
        ExistingCookies.None => [],
        ExistingCookies.OneUnrelated => ["unrelated-a=1; path=/"],
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
    private enum HeaderMode
    {
        Normal,
        ThrowsOnWrite
    }

    /// <summary>Owns one direct handler call's context, response, abort source, and doubles.</summary>
    private sealed class CompletionScope : IDisposable
    {
        private readonly ServiceProvider provider;

        internal CompletionScope(ExistingCookies existing)
        {
            Existing = CookiesFor(existing);
            Headers = new ScriptedHeaderDictionary();
            foreach (var cookie in Existing)
            {
                Headers.Append(HeaderNames.SetCookie, cookie);
            }

            Resolver = new ScriptedResolver();
            Authentication = new ScriptedAuthenticationService();
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(Authentication);
            services.AddSingleton<IManagementCurrentOperatorResolver>(Resolver);
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

        internal ScriptedAuthenticationService Authentication { get; }

        internal ScriptedResolver Resolver { get; }

        internal CancellationTokenSource Abort { get; } = new();

        internal DefaultHttpContext Context { get; }

        internal string[] SetCookie => Headers.Raw;

        public void Dispose()
        {
            provider.Dispose();
            Abort.Dispose();
            Body.Dispose();
        }
    }

    /// <summary>Answers a scripted resolver result and can cancel the caller inside Resolve.</summary>
    private sealed class ScriptedResolver : IManagementCurrentOperatorResolver
    {
        internal ManagementCurrentOperatorResult? Result { get; set; } =
            ManagementCurrentOperatorResult.Unauthenticated();

        internal Func<ManagementCurrentOperatorResult?>? OnResolve { get; set; }

        public ManagementCurrentOperatorResult? Resolve(ClaimsPrincipal? principal) =>
            OnResolve is { } onResolve ? onResolve() : Result;
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

        public override void SetLength(long value) { }

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
    /// A header dictionary that can be switched into refusing writes of <c>Set-Cookie</c> while the
    /// test still sees what the response really holds.
    /// </summary>
    private sealed class ScriptedHeaderDictionary : IHeaderDictionary
    {
        private readonly IHeaderDictionary inner = new HeaderDictionary();

        internal HeaderMode Mode { get; set; }

        /// <summary>What the response really holds, whatever mode writes are in.</summary>
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
            get => inner[key];
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

        public bool TryGetValue(string key, out StringValues value) =>
            inner.TryGetValue(key, out value);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            inner.GetEnumerator();

        private static bool IsSetCookie(string key) =>
            string.Equals(key, HeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase);

        private bool Writable(string key)
        {
            if (!IsSetCookie(key))
            {
                return true;
            }

            return Mode == HeaderMode.ThrowsOnWrite
                ? throw new InvalidOperationException(Sentinel)
                : true;
        }
    }

    /// <summary>Records the authentication calls a handler makes and scripts each call's ending.</summary>
    private sealed class ScriptedAuthenticationService : IAuthenticationService
    {
        private int authenticateCalls;
        private int signInCalls;
        private int signOutCalls;

        internal int AuthenticateCalls => Volatile.Read(ref authenticateCalls);

        internal int SignInCalls => Volatile.Read(ref signInCalls);

        internal int SignOutCalls => Volatile.Read(ref signOutCalls);

        /// <summary>Runs inside the sign-in, before it completes normally.</summary>
        internal Func<HttpContext, Task>? OnSignIn { get; set; }

        /// <summary>Runs inside the sign-out, before it completes normally.</summary>
        internal Func<HttpContext, Task>? OnSignOut { get; set; }

        /// <summary>Runs inside authenticate, before it answers.</summary>
        internal Action? OnAuthenticate { get; set; }

        internal AuthenticateResult? AuthenticateResult { get; set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Interlocked.Increment(ref authenticateCalls);
            OnAuthenticate?.Invoke();
            return Task.FromResult(AuthenticateResult ?? AuthenticateResult.NoResult());
        }

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
        }

        public async Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref signOutCalls);
            if (OnSignOut is { } onSignOut)
            {
                await onSignOut(context);
            }
        }
    }

    /// <summary>
    /// Starts a host around the real management cookie handler whose <c>SignedIn</c> callback can
    /// cancel the request after the cookie was appended, and captures the response cookies and the
    /// surfaced cancellation the moment the cancelled login leaves the pipeline.
    /// </summary>
    private sealed class CompletionHostFixture : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly HttpClient client;
        private readonly Func<string[]> observedSetCookie;
        private readonly Func<CancellationToken?> observedCancellation;
        private readonly Func<CancellationToken> requestToken;

        private CompletionHostFixture(
            WebApplication application,
            HttpClient client,
            Func<string[]> observedSetCookie,
            Func<CancellationToken?> observedCancellation,
            Func<CancellationToken> requestToken)
        {
            this.application = application;
            this.client = client;
            this.observedSetCookie = observedSetCookie;
            this.observedCancellation = observedCancellation;
            this.requestToken = requestToken;
        }

        internal CancellationToken RequestToken => requestToken();

        internal string[] ObservedSetCookie => observedSetCookie();

        internal CancellationToken? ObservedCancellation => observedCancellation();

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        internal static async Task<CompletionHostFixture> StartAsync(bool cancelOnSignedIn)
        {
            string[] observed = [];
            CancellationToken? surfaced = null;
            // The session handler captures RequestAborted once at entry, so the fixture must swap
            // the token before the pipeline runs, not inside the sign-in callback.
            var requestSource = new CancellationTokenSource[1];
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
            if (cancelOnSignedIn)
            {
                builder.Services.PostConfigure<CookieAuthenticationOptions>(
                    ManagementSessionDefaults.AuthenticationScheme,
                    options => options.Events.OnSignedIn = _ =>
                    {
                        // The real handler has already appended the cookie when this runs; the
                        // callback cancels the request token the handler captured and completes
                        // normally.
                        Volatile.Read(ref requestSource[0]).Cancel();
                        return Task.CompletedTask;
                    });
            }

            var application = builder.Build();
            try
            {
                // The capture wraps the whole pipeline, so it observes the response exactly as the
                // cancelled login leaves it, before the host turns the cancellation into an abort.
                application.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    catch (OperationCanceledException exception)
                    {
                        observed = context.Response.Headers[HeaderNames.SetCookie].ToArray();
                        surfaced = exception.CancellationToken;
                        throw;
                    }
                });
                application.Use((context, next) =>
                {
                    var source = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    Volatile.Write(ref requestSource[0], source);
                    context.Features.Get<IHttpRequestLifetimeFeature>()!.RequestAborted =
                        source.Token;
                    return next(context);
                });
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

            return new CompletionHostFixture(
                application,
                application.GetTestClient(),
                () => observed,
                () => surfaced,
                () => Volatile.Read(ref requestSource[0]).Token);
        }

        /// <summary>Sends a login that the fixture cancels from inside the sign-in callback.</summary>
        internal async Task SendCancelledLoginAsync()
        {
            try
            {
                using var response = await client.SendAsync(CreateLogin(), Token);
            }
            catch
            {
                // A cancelled request never completes normally; the pipeline capture already
                // recorded the response state and the surfaced token.
            }
        }

        internal Task<HttpResponseMessage> LoginAsync() => client.SendAsync(CreateLogin(), Token);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.DisposeAsync();
        }

        private static HttpRequestMessage CreateLogin()
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                ManagementApiDefaults.DefaultRootPath + ManagementEntryDefaults.SessionLoginPath);
            request.Headers.TryAddWithoutValidation(
                ManagementEntryDefaults.UnsafeRequestHeaderName,
                ManagementEntryDefaults.UnsafeRequestHeaderValue);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"password\":\"secret\"}"));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Content = content;
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

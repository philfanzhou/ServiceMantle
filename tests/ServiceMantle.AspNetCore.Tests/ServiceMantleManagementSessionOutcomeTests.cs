using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Pins the order in which the management session entries decide an outcome once an asynchronous
/// dependency has finished: caller cancellation, then this call's already expired login budget,
/// then whatever the dependency returned or threw.
/// </summary>
/// <remarks>
/// These call the handlers directly so the assertions are about what the handler decided, not about
/// what an aborted HTTP client happened to observe. Event order is established with cancellation
/// notifications and completion sources, never with a sleep that guesses at a timeout.
/// </remarks>
public sealed class ServiceMantleManagementSessionOutcomeTests
{
    private const string Sentinel = "sentinel-session-outcome-detail";

    private static readonly TimeSpan Budget = ServiceMantleManagementSessionOptions.MinimumLoginTimeout;

    /// <summary>The finite set of ways a login adapter can finish.</summary>
    public enum AdapterOutcome
    {
        Authenticated,
        Unauthenticated,
        Failed,
        Null,
        OrdinaryException,
        InternalCancellation
    }

    public static TheoryData<AdapterOutcome> AdapterOutcomes =>
    [
        AdapterOutcome.Authenticated,
        AdapterOutcome.Unauthenticated,
        AdapterOutcome.Failed,
        AdapterOutcome.Null,
        AdapterOutcome.OrdinaryException,
        AdapterOutcome.InternalCancellation
    ];

    [Theory]
    [MemberData(nameof(AdapterOutcomes))]
    public async Task An_expired_budget_never_signs_in_whatever_the_adapter_returned(AdapterOutcome outcome)
    {
        using var scope = new SessionScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The adapter leaves exactly when the budget cancels the token it was handed, so the
        // boundary is an event and not a guessed delay.
        var result = await ServiceMantleManagementSessionHandlers.LoginAsync(
            scope.Context,
            async (_, token) =>
            {
                entered.TrySetResult();
                await WaitForCancellationAsync(token);
                return Finish(outcome);
            },
            Budget);

        Assert.True(entered.Task.IsCompletedSuccessfully);
        Assert.Same(ServiceMantleManagementSessionResult.Unavailable, result);
        Assert.Equal(0, scope.Authentication.SignInCalls);
        Assert.False(scope.Context.Response.HasStarted);
    }

    [Theory]
    [MemberData(nameof(AdapterOutcomes))]
    public async Task Caller_cancellation_outranks_every_adapter_outcome(AdapterOutcome outcome)
    {
        using var scope = new SessionScope();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ServiceMantleManagementSessionHandlers.LoginAsync(
                scope.Context,
                async (_, _) =>
                {
                    // The request is aborted strictly before the adapter finishes.
                    await scope.Abort.CancelAsync();
                    return Finish(outcome);
                },
                // A budget long enough that it can play no part in this outcome.
                ServiceMantleManagementSessionOptions.MaximumLoginTimeout));

        AssertSafeCallerCancellation(exception, scope.Abort.Token);
        Assert.Equal(0, scope.Authentication.SignInCalls);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_the_budget_expired_too()
    {
        using var scope = new SessionScope();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ServiceMantleManagementSessionHandlers.LoginAsync(
                scope.Context,
                async (_, token) =>
                {
                    await WaitForCancellationAsync(token);
                    await scope.Abort.CancelAsync();
                    return Finish(AdapterOutcome.Authenticated);
                },
                Budget));

        AssertSafeCallerCancellation(exception, scope.Abort.Token);
        Assert.Equal(0, scope.Authentication.SignInCalls);
    }

    [Fact]
    public async Task An_ordinary_sign_in_failure_is_fixed_unless_the_caller_already_cancelled()
    {
        using var uncancelled = new SessionScope();
        uncancelled.Authentication.SignInFailure = () => new InvalidOperationException(Sentinel);

        var fixedResult = await ServiceMantleManagementSessionHandlers.LoginAsync(
            uncancelled.Context,
            (_, _) => ValueTask.FromResult(Authenticated()),
            ServiceMantleManagementSessionOptions.MaximumLoginTimeout);

        Assert.Same(ServiceMantleManagementSessionResult.Unavailable, fixedResult);
        Assert.Equal(1, uncancelled.Authentication.SignInCalls);

        using var cancelled = new SessionScope();
        cancelled.Authentication.SignInFailure = () =>
        {
            cancelled.Abort.Cancel();
            return new InvalidOperationException(Sentinel);
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ServiceMantleManagementSessionHandlers.LoginAsync(
                cancelled.Context,
                (_, _) => ValueTask.FromResult(Authenticated()),
                ServiceMantleManagementSessionOptions.MaximumLoginTimeout));

        AssertSafeCallerCancellation(exception, cancelled.Abort.Token);
        Assert.Equal(1, cancelled.Authentication.SignInCalls);
    }

    [Fact]
    public async Task An_ordinary_authenticate_failure_is_fixed_unless_the_caller_already_cancelled()
    {
        using var uncancelled = new SessionScope(Resolver());
        uncancelled.Authentication.AuthenticateFailure = () => new InvalidOperationException(Sentinel);

        Assert.Same(
            ServiceMantleManagementSessionResult.Unavailable,
            await ServiceMantleManagementSessionHandlers.CurrentAsync(uncancelled.Context));

        using var cancelled = new SessionScope(Resolver());
        cancelled.Authentication.AuthenticateFailure = () =>
        {
            cancelled.Abort.Cancel();
            return new InvalidOperationException(Sentinel);
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ServiceMantleManagementSessionHandlers.CurrentAsync(cancelled.Context));

        AssertSafeCallerCancellation(exception, cancelled.Abort.Token);
    }

    [Fact]
    public async Task An_ordinary_sign_out_failure_is_fixed_unless_the_caller_already_cancelled()
    {
        using var uncancelled = new SessionScope();
        uncancelled.Authentication.SignOutFailure = () => new InvalidOperationException(Sentinel);

        Assert.Same(
            ServiceMantleManagementSessionResult.Unavailable,
            await ServiceMantleManagementSessionHandlers.LogoutAsync(uncancelled.Context));
        Assert.Equal(1, uncancelled.Authentication.SignOutCalls);

        using var cancelled = new SessionScope();
        cancelled.Authentication.SignOutFailure = () =>
        {
            cancelled.Abort.Cancel();
            return new InvalidOperationException(Sentinel);
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ServiceMantleManagementSessionHandlers.LogoutAsync(cancelled.Context));

        AssertSafeCallerCancellation(exception, cancelled.Abort.Token);
        Assert.Equal(1, cancelled.Authentication.SignOutCalls);
    }

    [Fact]
    public async Task An_uncancelled_login_within_its_budget_keeps_its_existing_result()
    {
        foreach (var (outcome, expected, expectedSignIns) in new[]
                 {
                     (AdapterOutcome.Authenticated, ServiceMantleManagementSessionResult.NoContent, 1),
                     (AdapterOutcome.Unauthenticated, ServiceMantleManagementSessionResult.Unauthenticated, 0),
                     (AdapterOutcome.Failed, ServiceMantleManagementSessionResult.Unavailable, 0),
                     (AdapterOutcome.Null, ServiceMantleManagementSessionResult.Unavailable, 0)
                 })
        {
            using var scope = new SessionScope();

            var result = await ServiceMantleManagementSessionHandlers.LoginAsync(
                scope.Context,
                (_, _) => ValueTask.FromResult(Finish(outcome)),
                ServiceMantleManagementSessionOptions.MaximumLoginTimeout);

            Assert.Same(expected, result);
            Assert.Equal(expectedSignIns, scope.Authentication.SignInCalls);
        }
    }

    [Fact]
    public async Task The_current_session_and_logout_keep_their_normal_results()
    {
        using var current = new SessionScope(Resolver());
        Assert.IsType<ServiceMantleManagementSessionResult>(
            await ServiceMantleManagementSessionHandlers.CurrentAsync(current.Context));
        Assert.Equal(1, current.Authentication.AuthenticateCalls);

        using var logout = new SessionScope();
        Assert.Same(
            ServiceMantleManagementSessionResult.NoContent,
            await ServiceMantleManagementSessionHandlers.LogoutAsync(logout.Context));
        Assert.Equal(1, logout.Authentication.SignOutCalls);
    }

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception,
        CancellationToken callerToken)
    {
        Assert.Equal(callerToken, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task;
    }

    private static ManagementIdentityResult Finish(AdapterOutcome outcome) => outcome switch
    {
        AdapterOutcome.Authenticated => Authenticated(),
        AdapterOutcome.Unauthenticated => ManagementIdentityResult.Unauthenticated(),
        AdapterOutcome.Failed => ManagementIdentityResult.Failed("provider.sentinel_upstream_code"),
        AdapterOutcome.Null => null!,
        AdapterOutcome.OrdinaryException => throw new InvalidOperationException(Sentinel),
        _ => throw new OperationCanceledException(Sentinel, new CancellationTokenSource().Token)
    };

    private static ManagementIdentityResult Authenticated() =>
        ManagementIdentityResult.Authenticated(Identity());

    private static ManagementIdentity Identity() => ManagementIdentity.Create(
        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
        "operator-1",
        [ManagementPermission.Read, ManagementPermission.Admin],
        "sensitive-display-name");

    private static IManagementCurrentOperatorResolver Resolver() => new FixedOperatorResolver();

    private sealed class FixedOperatorResolver : IManagementCurrentOperatorResolver
    {
        public ManagementCurrentOperatorResult Resolve(ClaimsPrincipal? principal) =>
            ManagementCurrentOperatorResult.Resolved(Identity());
    }

    /// <summary>Owns one handler call's context, request abort source, and recorded auth calls.</summary>
    private sealed class SessionScope : IDisposable
    {
        private readonly ServiceProvider provider;

        internal SessionScope(IManagementCurrentOperatorResolver? resolver = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(Authentication);
            if (resolver is not null)
            {
                services.AddSingleton(resolver);
            }

            provider = services.BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = provider, RequestAborted = Abort.Token };
            Context.Request.Method = HttpMethods.Post;
            Context.Request.Body = Stream.Null;
        }

        internal RecordingAuthenticationService Authentication { get; } = new();

        internal CancellationTokenSource Abort { get; } = new();

        internal DefaultHttpContext Context { get; }

        public void Dispose()
        {
            provider.Dispose();
            Abort.Dispose();
        }
    }

    /// <summary>
    /// Counts the authentication calls a handler makes and can fail one of them, optionally after
    /// aborting the request first.
    /// </summary>
    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        internal int SignInCalls { get; private set; }

        internal int SignOutCalls { get; private set; }

        internal int AuthenticateCalls { get; private set; }

        internal Func<Exception>? SignInFailure { get; set; }

        internal Func<Exception>? SignOutFailure { get; set; }

        internal Func<Exception>? AuthenticateFailure { get; set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            AuthenticateCalls++;
            if (AuthenticateFailure is { } failure)
            {
                return Task.FromException<AuthenticateResult>(failure());
            }

            var ticket = new AuthenticationTicket(
                Identity().ToClaimsPrincipal(),
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) },
                ServiceMantleManagementSessionDefaults.AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

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
            return SignInFailure is { } failure ? Task.FromException(failure()) : Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            SignOutCalls++;
            return SignOutFailure is { } failure ? Task.FromException(failure()) : Task.CompletedTask;
        }
    }
}

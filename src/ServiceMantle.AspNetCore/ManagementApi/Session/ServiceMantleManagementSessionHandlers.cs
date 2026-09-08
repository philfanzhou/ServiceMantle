using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Answers the three management session entries: login, the current-session read, and local logout.
/// </summary>
/// <remarks>
/// The handlers issue a cookie only after a provider result is authenticated and the fixed-scheme
/// sign-in completed, and they never copy a provider error code into a response or a ServiceMantle
/// diagnostic: a syntactically valid consumer string is not proof that it is non-secret.
/// </remarks>
internal static class ServiceMantleManagementSessionHandlers
{
    /// <summary>
    /// Admits at most 64 KiB of credential material, counted as it is read, calls the consumer
    /// login adapter under the login budget, and signs in only an authenticated identity.
    /// </summary>
    internal static async Task<IResult> LoginAsync(
        HttpContext context,
        ServiceMantleManagementLoginAdapter adapter,
        TimeSpan loginTimeout)
    {
        var cancellationToken = context.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();

        var request = context.Request;
        if (request.QueryString.HasValue ||
            request.Headers.ContainsKey(HeaderNames.ContentEncoding) ||
            request.ContentLength > ServiceMantleManagementSessionMapping.MaximumLoginBodyLength)
        {
            return ServiceMantleManagementApiResults.InvalidRequest();
        }

        // One budget covers the body read and the adapter call and is not reset between them.
        using var budget = new CancellationTokenSource(loginTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            budget.Token);

        // The envelope is enforced by counting the bytes that actually arrive, so the host's own
        // request size limit is left exactly as the host set it: it is never widened, and a host
        // that already admits less still rejects first. Lowering it here would reject a body inside
        // the envelope, because a server that counts a chunked request counts its framing too. The
        // media type and the schema inside the envelope stay the consumer adapter's obligation.
        using var body = await ServiceMantleManagementSessionBodyAdmission
            .ReadAsync(request, linked.Token)
            .ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        switch (body.Status)
        {
            case ServiceMantleManagementSessionBodyAdmission
                .ServiceMantleManagementSessionBodyStatus.Rejected:
                // An oversized body is the request's fault and is answered before the adapter runs,
                // whether this handler or the host counted the overrun.
                return ServiceMantleManagementApiResults.InvalidRequest();
            case ServiceMantleManagementSessionBodyAdmission
                .ServiceMantleManagementSessionBodyStatus.Unavailable:
                return ServiceMantleManagementSessionResult.Unavailable;
        }

        if (budget.Token.IsCancellationRequested)
        {
            // The budget was already spent reading the body, so the adapter is not called at all.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        ManagementIdentityResult? result;
        try
        {
            result = await InvokeAsync(context, adapter, body, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            // The caller's cancellation outranks how the adapter left, so an ordinary failure
            // raised after the request was aborted is not reported as a fixed result either.
            if (cancellationToken.IsCancellationRequested)
            {
                throw CancelledByCaller(linked, cancellationToken);
            }

            // An adapter failure, an internal timeout, and an unrelated internal cancellation are
            // one safe outcome, and no provider code or exception is projected.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        if (budget.Token.IsCancellationRequested)
        {
            // The budget for this adapter call is spent. An adapter that returns after its own
            // deadline has passed still returns too late, so nothing it says is signed in.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        if (result?.Status == ManagementIdentityStatus.Unauthenticated)
        {
            return ServiceMantleManagementSessionResult.Unauthenticated;
        }

        if (result?.Status != ManagementIdentityStatus.Authenticated || result.Identity is null)
        {
            // Failed, null, an undefined status, and an authenticated result without an identity
            // are the same safe outcome. A consumer-supplied source error code is never forwarded.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        string?[] snapshot;
        try
        {
            if (context.Response.HasStarted)
            {
                return ServiceMantleManagementSessionResult.Unavailable;
            }

            // The response's own Set-Cookie values are captured before the sign-in, so a failure
            // has an exact state to return to instead of guessing which values were this ticket's.
            snapshot = SetCookieSnapshot(context.Response);
        }
        catch
        {
            // Without a snapshot a failed sign-in could not be rolled back, so none is started.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        try
        {
            await context.SignInAsync(
                    ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                    result.Identity.ToClaimsPrincipal())
                .ConfigureAwait(false);
        }
        catch
        {
            // The rollback is the shared exit of every sign-in failure and runs before the caller's
            // cancellation is answered, so a cancelled caller still gets its own token back. A
            // response that already started keeps what it sent: repairing it is a declared
            // non-guarantee, and no second body is written for it either.
            var terminated = !context.Response.HasStarted &&
                !TryRestoreSetCookie(context.Response, snapshot);
            if (terminated)
            {
                // The response may still carry a complete or chunked part of this failed ticket and
                // cannot be repaired, so the connection is aborted rather than completed.
                context.Abort();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw CancelledByCaller(linked, cancellationToken);
            }

            // A failed sign-in sends no cookie, so the caller must not be told it has a session.
            return terminated
                ? ServiceMantleManagementSessionResult.Terminated
                : ServiceMantleManagementSessionResult.Unavailable;
        }

        return ServiceMantleManagementSessionResult.NoContent;
    }

    /// <summary>
    /// Runs the adapter over the admitted body copy. The request's own stream and pipe feature are
    /// put back on every exit, including a failure or a cancellation, so nothing downstream keeps
    /// reading this handler's buffer.
    /// </summary>
    private static async ValueTask<ManagementIdentityResult?> InvokeAsync(
        HttpContext context,
        ServiceMantleManagementLoginAdapter adapter,
        ServiceMantleManagementSessionBodyAdmission body,
        CancellationToken loginToken)
    {
        body.Install(context);
        try
        {
            return await adapter(context, loginToken).ConfigureAwait(false);
        }
        finally
        {
            body.Restore();
        }
    }

    /// <summary>Projects the fixed three fields of the current session.</summary>
    internal static async Task<IResult> CurrentAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();

        var resolution = context.RequestServices
            .GetRequiredService<IManagementCurrentOperatorResolver>()
            .Resolve(context.User);
        if (resolution?.Status != ManagementCurrentOperatorStatus.Resolved ||
            resolution.Identity is null)
        {
            // The session policy already admitted this request, so a principal that no longer
            // resolves keeps the existing forbidden contract rather than inventing a status code.
            return Results.Forbid();
        }

        AuthenticateResult authentication;
        try
        {
            // The cookie handler caches its own result for the request, so this reads the ticket
            // that already authenticated the caller rather than authenticating a second time.
            authentication = await context
                .AuthenticateAsync(ServiceMantleManagementSessionDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
        }
        catch
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                throw CancelledByCaller(context.RequestAborted);
            }

            return ServiceMantleManagementSessionResult.Unavailable;
        }

        return authentication.Succeeded && authentication.Properties?.ExpiresUtc is { } expiresUtc
            ? ServiceMantleManagementSessionResult.Current(resolution.Identity, expiresUtc)
            // The expiry is one of the three fixed fields; a ticket without one is not projected
            // with a guessed value.
            : ServiceMantleManagementSessionResult.Unavailable;
    }

    /// <summary>Deletes this client's own management cookie and nothing else.</summary>
    internal static async Task<IResult> LogoutAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();

        try
        {
            if (context.Response.HasStarted)
            {
                return ServiceMantleManagementSessionResult.Unavailable;
            }

            await context
                .SignOutAsync(ServiceMantleManagementSessionDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
        }
        catch
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                throw CancelledByCaller(context.RequestAborted);
            }

            // Success is claimed only once the cookie deletion is on the response.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        return ServiceMantleManagementSessionResult.NoContent;
    }

    /// <summary>
    /// Copies the response's current <c>Set-Cookie</c> values out of the header. The copy is what a
    /// failed sign-in is restored to, so it must not share the array the sign-in appends to or
    /// replaces.
    /// </summary>
    private static string?[] SetCookieSnapshot(HttpResponse response)
    {
        var existing = response.Headers[HeaderNames.SetCookie];
        var snapshot = new string?[existing.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = existing[index];
        }

        return snapshot;
    }

    /// <summary>
    /// Puts the snapshot back, dropping every value this sign-in appended or replaced and keeping
    /// the unrelated cookies that were already there, in their original count and order. It reports
    /// success only once the response reads the snapshot back: a header dictionary that refused or
    /// ignored the restore still holds the failed ticket.
    /// </summary>
    private static bool TryRestoreSetCookie(HttpResponse response, string?[] snapshot)
    {
        try
        {
            if (snapshot.Length == 0)
            {
                response.Headers.Remove(HeaderNames.SetCookie);
            }
            else
            {
                response.Headers[HeaderNames.SetCookie] = new StringValues(snapshot);
            }

            var restored = response.Headers[HeaderNames.SetCookie];
            if (restored.Count != snapshot.Length)
            {
                return false;
            }

            for (var index = 0; index < snapshot.Length; index++)
            {
                if (!string.Equals(restored[index], snapshot[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // An unwritable or throwing header dictionary leaves the ticket where it is; the caller
            // terminates the response instead of sending it.
            return false;
        }
    }

    /// <summary>
    /// Owns the cancellation exit: the adapter is notified on the token it received before the
    /// linked source is released, and the caller still observes its own cancellation.
    /// </summary>
    private static OperationCanceledException CancelledByCaller(
        CancellationTokenSource linked,
        CancellationToken cancellationToken)
    {
        try
        {
            linked.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation callbacks that throw are outside the cooperative cancellation contract
            // and must not replace the caller's cancellation result.
        }

        return CancelledByCaller(cancellationToken);
    }

    /// <summary>
    /// Builds the caller's own cancellation result. It carries the request token and nothing else:
    /// no dependency exception, no provider code, and no consumer text.
    /// </summary>
    private static OperationCanceledException CancelledByCaller(CancellationToken cancellationToken) =>
        new("The management session request was cancelled by the caller.", cancellationToken);
}

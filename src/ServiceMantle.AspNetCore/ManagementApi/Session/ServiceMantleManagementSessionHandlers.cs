using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
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
    /// Admits at most 64 KiB of credential material, calls the consumer login adapter under the
    /// login budget, and signs in only an authenticated identity.
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

        // A body without a declared length is bounded by the server feature instead, so a chunked
        // upload cannot exceed the admission envelope either. The media type and the schema inside
        // that envelope stay the consumer adapter's obligation.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ServiceMantleManagementSessionMapping.MaximumLoginBodyLength;
        }

        using var budget = new CancellationTokenSource(loginTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            budget.Token);

        ManagementIdentityResult? result;
        try
        {
            result = await adapter(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }
        catch
        {
            // An adapter failure, an internal timeout, and an unrelated internal cancellation are
            // one safe outcome, and no provider code or exception is projected.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
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

        try
        {
            if (context.Response.HasStarted)
            {
                return ServiceMantleManagementSessionResult.Unavailable;
            }

            await context.SignInAsync(
                    ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                    result.Identity.ToClaimsPrincipal())
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failed sign-in sends no cookie, so the caller must not be told it has a session.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        return ServiceMantleManagementSessionResult.NoContent;
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Success is claimed only once the cookie deletion is on the response.
            return ServiceMantleManagementSessionResult.Unavailable;
        }

        return ServiceMantleManagementSessionResult.NoContent;
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

        return new OperationCanceledException(
            "The management login was cancelled by the caller.",
            cancellationToken);
    }
}

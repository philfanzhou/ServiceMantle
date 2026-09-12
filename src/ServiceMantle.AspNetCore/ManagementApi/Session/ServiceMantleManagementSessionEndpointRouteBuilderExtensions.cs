using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Session;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the management identity login, current-session, and logout entries.
/// </summary>
public static class ServiceMantleManagementSessionEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST {versionedRoot}/session/login</c>, <c>GET</c>/<c>HEAD {versionedRoot}/session</c>,
    /// and <c>POST {versionedRoot}/session/logout</c> with the shared management entry baseline.
    /// </summary>
    /// <param name="endpoints">The application the ServiceMantle pipeline was composed on.</param>
    /// <param name="loginAdapter">
    /// The consumer login adapter. It obtains credentials through its own trusted scoped accessor
    /// and calls the existing provider SPI; ServiceMantle passes no credential object.
    /// </param>
    /// <param name="configure">Optional endpoint-local session options.</param>
    /// <returns>The same endpoint route builder.</returns>
    /// <remarks>
    /// The entries are opt-in and are mapped beside the protected group returned by
    /// <c>MapServiceMantleManagementApiV1</c>, never inside it, so the login exception exists only in
    /// the shared entry baseline and the Admin group keeps rejecting anonymous access. The shared
    /// baseline admits all three only in <c>Completed + Succeeded + Reachable</c>, keeps login
    /// anonymous under the Setup rate-limit policy, requires any legitimate ServiceMantle management
    /// identity - not Admin - for the read and the logout under the management operator policy, adds
    /// the security response headers and the correlation contract, and requires exactly one
    /// <c>X-ServiceMantle-Request: 1</c> header on both <c>POST</c> entries.
    /// <para>
    /// Login admits at most 64 KiB of raw body and rejects a query string or a content encoding
    /// before the adapter runs; the media type and the schema inside that envelope are the adapter's
    /// obligation. The envelope is counted while the body is read, so a request with no declared
    /// length meets the same limit as one that declares it and a declared length below the limit
    /// excuses nothing: at most one byte past the envelope is ever read, and it is read into memory
    /// and never onto disk. The host's own request size limit is left as the host set it - never
    /// widened, and never narrowed - and a host that admits less still rejects first. An oversized
    /// body and a host's own <c>413</c> are the fixed management <c>400</c>, and neither runs the
    /// adapter. An authenticated result is converted to a ServiceMantle claims principal and signed
    /// in on the fixed cookie scheme, and only a completed sign-in answers <c>204</c>. An
    /// unauthenticated result answers the existing session <c>401</c>. A failed, null, or invalid
    /// result, an adapter exception, a body read failure, an internal cancellation, an internal
    /// timeout, and a failed sign-in all answer
    /// <c>503 {"errorCode":"management.session.unavailable"}</c>; a consumer-supplied provider error
    /// code is never forwarded.
    /// </para>
    /// <para>
    /// The adapter is handed the admitted copy: <c>Request.Body</c> and a <c>Request.BodyReader</c>
    /// obtained inside the call both read the complete original bytes, and the request's own stream
    /// and body pipe feature are put back when the call ends, however it ended. One login budget
    /// covers the body read and the adapter call together and is not reset between them.
    /// </para>
    /// <para>
    /// A sign-in that appended or replaced <c>Set-Cookie</c> values before it failed is rolled back
    /// to the snapshot taken before it started, so a failed login sends no complete or chunked part
    /// of that ticket and keeps the cookies the response already carried, in their original count
    /// and order. The same rollback covers a sign-in that completed after the request aborted: its
    /// ticket is restored before the caller's cancellation is answered and the fixed <c>204</c> is
    /// not delivered. The rollback runs before the caller's cancellation is answered and uses no
    /// sign-out compensation. A response that has already started is left as sent, an unreadable
    /// snapshot starts no sign-in, and a restore that cannot be applied aborts the connection
    /// instead of completing a response that still carries part of the ticket.
    /// </para>
    /// <para>
    /// The current-session read answers exactly <c>authenticated</c>, the UTC <c>expiresAtUtc</c>,
    /// and the defined permission names in their fixed order. It returns no operator identifier,
    /// display name, source, claims, authentication properties, ticket material, or provider data,
    /// and <c>HEAD</c> answers the same status and headers with no body. Logout calls the fixed
    /// scheme's sign-out and answers <c>204</c> only once the cookie deletion is on the response; it
    /// is local and stateless and revokes no copied ticket. Caller cancellation propagates its
    /// original token: the operator resolver, the authenticate call, and the sign-out are all
    /// observed once they settle, and a completion seen after the request aborted - including a
    /// logout's written deletion cookie, which stays on the response - answers the cancellation
    /// instead of a normal result.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The login adapter is missing, the login timeout is outside 100 milliseconds through 30
    /// seconds, the shared management entry capability or management API v1 capability is not
    /// registered, or the entries are mapped more than once.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleManagementSession(
        this IEndpointRouteBuilder endpoints,
        ManagementLoginAdapter? loginAdapter,
        Action<ManagementSessionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (loginAdapter is null)
        {
            throw ManagementSessionMapping.MissingAdapter();
        }

        var options = new ManagementSessionOptions();
        configure?.Invoke(options);
        var loginTimeout = options.LoginTimeout;
        if (loginTimeout < ManagementSessionOptions.MinimumLoginTimeout ||
            loginTimeout > ManagementSessionOptions.MaximumLoginTimeout)
        {
            throw ManagementSessionMapping.InvalidLoginTimeout();
        }

        ManagementSessionMapping.RecordMap(endpoints.ServiceProvider);
        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.SessionLogin,
            (HttpContext context) =>
                ManagementSessionHandlers.LoginAsync(context, loginAdapter, loginTimeout));
        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.CurrentSession,
            (HttpContext context) => ManagementSessionHandlers.CurrentAsync(context));
        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.SessionLogout,
            (HttpContext context) => ManagementSessionHandlers.LogoutAsync(context));
        return endpoints;
    }
}

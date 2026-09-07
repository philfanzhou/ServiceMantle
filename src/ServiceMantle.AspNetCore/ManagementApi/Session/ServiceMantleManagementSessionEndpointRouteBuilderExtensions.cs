using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ServiceMantle.AspNetCore;
using ServiceMantle.Management;

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
    /// obligation. An authenticated result is converted to a ServiceMantle claims principal and
    /// signed in on the fixed cookie scheme, and only a completed sign-in answers <c>204</c>. An
    /// unauthenticated result answers the existing session <c>401</c>. A failed, null, or invalid
    /// result, an adapter exception, an internal cancellation, an internal timeout, and a failed
    /// sign-in all answer <c>503 {"errorCode":"management.session.unavailable"}</c> and send no
    /// partial cookie; a consumer-supplied provider error code is never forwarded.
    /// </para>
    /// <para>
    /// The current-session read answers exactly <c>authenticated</c>, the UTC <c>expiresAtUtc</c>,
    /// and the defined permission names in their fixed order. It returns no operator identifier,
    /// display name, source, claims, authentication properties, ticket material, or provider data,
    /// and <c>HEAD</c> answers the same status and headers with no body. Logout calls the fixed
    /// scheme's sign-out and answers <c>204</c> only once the cookie deletion is on the response; it
    /// is local and stateless and revokes no copied ticket. Caller cancellation propagates its
    /// original token.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The login adapter is missing, the login timeout is outside 100 milliseconds through 30
    /// seconds, the shared management entry capability or management API v1 capability is not
    /// registered, or the entries are mapped more than once.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleManagementSession(
        this IEndpointRouteBuilder endpoints,
        ServiceMantleManagementLoginAdapter? loginAdapter,
        Action<ServiceMantleManagementSessionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (loginAdapter is null)
        {
            throw ServiceMantleManagementSessionMapping.MissingAdapter();
        }

        var options = new ServiceMantleManagementSessionOptions();
        configure?.Invoke(options);
        var loginTimeout = options.LoginTimeout;
        if (loginTimeout < ServiceMantleManagementSessionOptions.MinimumLoginTimeout ||
            loginTimeout > ServiceMantleManagementSessionOptions.MaximumLoginTimeout)
        {
            throw ServiceMantleManagementSessionMapping.InvalidLoginTimeout();
        }

        ServiceMantleManagementSessionMapping.RecordMap(endpoints.ServiceProvider);
        endpoints.MapServiceMantleManagementEntry(
            ServiceMantleManagementEntryKind.SessionLogin,
            (HttpContext context) =>
                ServiceMantleManagementSessionHandlers.LoginAsync(context, loginAdapter, loginTimeout));
        endpoints.MapServiceMantleManagementEntry(
            ServiceMantleManagementEntryKind.CurrentSession,
            (HttpContext context) => ServiceMantleManagementSessionHandlers.CurrentAsync(context));
        endpoints.MapServiceMantleManagementEntry(
            ServiceMantleManagementEntryKind.SessionLogout,
            (HttpContext context) => ServiceMantleManagementSessionHandlers.LogoutAsync(context));
        return endpoints;
    }
}

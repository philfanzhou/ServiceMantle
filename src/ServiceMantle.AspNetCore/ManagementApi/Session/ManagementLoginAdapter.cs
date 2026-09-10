using Microsoft.AspNetCore.Http;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore.ManagementApi.Session;

/// <summary>
/// Obtains one management identity for a login request from credentials the consuming service owns.
/// </summary>
/// <param name="httpContext">The current login request.</param>
/// <param name="cancellationToken">
/// The login token: the request token, additionally bounded by the endpoint's login budget.
/// </param>
/// <returns>The closed three-state identity result.</returns>
/// <remarks>
/// <para>
/// ServiceMantle defines no universal credential schema. The adapter parses whatever media type and
/// body shape the consuming service actually supports, places the credentials only into its own
/// trusted scoped credential accessor, and calls
/// <see cref="ManagementIdentityProviderInvoker.InvokeAsync"/>. The public core provider SPI still
/// receives no credential object.
/// </para>
/// <para>
/// The adapter must not retain or return raw credentials, must not write to the response, and must
/// not sign anything in. ServiceMantle admits at most 64 KiB of raw request body, counted as the
/// body is read rather than taken from a declared length, and rejects a query string or a content
/// encoding before the adapter runs; strict media type and schema validation inside that envelope
/// remains the adapter's obligation. An adapter that ignores its token cannot be forcibly
/// terminated.
/// </para>
/// <para>
/// The whole admitted body is already in memory when the adapter is called, so
/// <see cref="HttpContext.Request"/> hands it the complete copy through
/// <see cref="HttpRequest.Body"/> or through a <see cref="HttpRequest.BodyReader"/> obtained inside
/// the call. Reading less than all of it, or nothing at all, cannot widen the envelope. The two
/// readers are alternatives, not a sequence: interleaving them within one call has no defined
/// result, and the original request stream is restored when the adapter returns.
/// </para>
/// <para>
/// How the adapter left is decided after it returns or throws, in a fixed order: the caller's own
/// cancellation first, then this call's already expired login budget, then the returned result or
/// exception. An adapter that returns an authenticated identity after its budget has passed
/// returned too late, and nothing it says is signed in.
/// </para>
/// </remarks>
public delegate ValueTask<ManagementIdentityResult> ManagementLoginAdapter(
    HttpContext httpContext,
    CancellationToken cancellationToken);

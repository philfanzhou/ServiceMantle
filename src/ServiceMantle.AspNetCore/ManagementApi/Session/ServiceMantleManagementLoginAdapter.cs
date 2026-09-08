using Microsoft.AspNetCore.Http;

namespace ServiceMantle.Management;

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
/// not sign anything in. ServiceMantle admits at most 64 KiB of raw request body and rejects a query
/// string or a content encoding before the adapter runs; strict media type and schema validation
/// inside that envelope remains the adapter's obligation. An adapter that ignores its token cannot
/// be forcibly terminated.
/// </para>
/// <para>
/// How the adapter left is decided after it returns or throws, in a fixed order: the caller's own
/// cancellation first, then this call's already expired login budget, then the returned result or
/// exception. An adapter that returns an authenticated identity after its budget has passed
/// returned too late, and nothing it says is signed in.
/// </para>
/// </remarks>
public delegate ValueTask<ManagementIdentityResult> ServiceMantleManagementLoginAdapter(
    HttpContext httpContext,
    CancellationToken cancellationToken);

/// <summary>
/// Configures the endpoint-local behaviour of the management login entry.
/// </summary>
/// <remarks>
/// These values belong to the login entry only. They do not change the management cookie lifetime,
/// the sliding renewal behaviour, the Data Protection key ring, or any global clock.
/// </remarks>
public sealed class ServiceMantleManagementSessionOptions
{
    /// <summary>The default login budget.</summary>
    public static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The smallest accepted login budget.</summary>
    public static readonly TimeSpan MinimumLoginTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The largest accepted login budget.</summary>
    public static readonly TimeSpan MaximumLoginTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the internal budget for one login adapter call, defaulting to ten seconds.
    /// </summary>
    /// <remarks>
    /// Valid values are 100 milliseconds through 30 seconds; anything else fails before the host
    /// starts. The budget bounds waiting on a cooperative adapter. It is not a hard wall-clock
    /// bound: an adapter or provider that ignores its cancellation token cannot be terminated.
    /// </remarks>
    /// <remarks>
    /// The budget still takes part in the outcome once the adapter has finished: a login whose
    /// budget had already expired is the fixed unavailable result and issues no cookie, whatever
    /// the adapter returned or threw. Only the caller's own cancellation outranks it. The budget
    /// covers this login adapter call alone - not the cookie lifetime, and not any other session
    /// operation.
    /// </remarks>
    public TimeSpan LoginTimeout { get; set; } = DefaultLoginTimeout;
}

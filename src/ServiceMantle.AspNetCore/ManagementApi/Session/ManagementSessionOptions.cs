namespace ServiceMantle.AspNetCore.ManagementApi.Session;

/// <summary>
/// Configures the endpoint-local behaviour of the management login entry.
/// </summary>
/// <remarks>
/// These values belong to the login entry only. They do not change the management cookie lifetime,
/// the sliding renewal behaviour, the Data Protection key ring, or any global clock.
/// </remarks>
public sealed class ManagementSessionOptions
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
    /// starts. One budget covers reading the raw request body and the adapter call that follows it,
    /// and it is not reset between them: a login whose budget was spent reading the body answers the
    /// fixed unavailable result and never calls the adapter. The budget bounds waiting on a
    /// cooperative adapter and a cooperative body stream. It is not a hard wall-clock bound: an
    /// adapter, provider, or stream that ignores its cancellation token cannot be terminated.
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

namespace ServiceMantle.Health;

/// <summary>
/// Well-known safe failures produced while resolving one final readiness decision.
/// </summary>
public static class WellKnownServiceReadinessDecisionErrorCodes
{
    /// <summary>The base health snapshot exceeded the decision source's internal budget.</summary>
    public const string ProbeTimeout = "health.probe_timeout";

    /// <summary>
    /// The base health snapshot source was missing, failed, or produced no snapshot.
    /// </summary>
    public const string ProbeFailed = "health.probe_failed";
}

/// <summary>
/// One immutable final readiness decision produced by a single evaluation.
/// </summary>
/// <remarks>
/// A decision carries the exact <see cref="ServiceHealthSnapshot"/> the evaluation used, the final
/// Ready value after the base matrix and the ordered readiness contributors, and a bounded safe
/// error code only when it is not Ready. A ready decision always carries a base-ready snapshot, so a
/// replacement source cannot turn a base-unready snapshot into Ready. The value never carries
/// exceptions, stack traces, or other unbounded diagnostic material.
/// </remarks>
public sealed class ServiceReadinessDecision
{
    private ServiceReadinessDecision(
        ServiceHealthSnapshot? snapshot,
        bool isReady,
        string? errorCode)
    {
        Snapshot = snapshot;
        IsReady = isReady;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the exact snapshot used by this evaluation, or null when no snapshot was obtained.
    /// </summary>
    public ServiceHealthSnapshot? Snapshot { get; }

    /// <summary>Gets the final readiness value for the whole evaluation.</summary>
    public bool IsReady { get; }

    /// <summary>
    /// Gets the bounded safe error code, or null when the decision is Ready or the snapshot
    /// itself carried no code.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a ready decision from the base-ready snapshot that produced it.</summary>
    /// <param name="snapshot">The exact snapshot the evaluation used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="snapshot"/> is not ready under <see cref="ServiceHealthEvaluator"/>.
    /// </exception>
    public static ServiceReadinessDecision Ready(ServiceHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ServiceHealthEvaluator.Evaluate(snapshot).IsReady)
        {
            throw new ArgumentException(
                "A ready readiness decision requires a snapshot that the base matrix accepts.",
                nameof(snapshot));
        }

        return new ServiceReadinessDecision(snapshot, isReady: true, errorCode: null);
    }

    /// <summary>Creates a not-ready decision that keeps the snapshot it was evaluated from.</summary>
    /// <param name="snapshot">The exact snapshot the evaluation used.</param>
    /// <param name="errorCode">An optional stable safe error code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is not a safe code.</exception>
    public static ServiceReadinessDecision NotReady(
        ServiceHealthSnapshot snapshot,
        string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ServiceReadinessDecision(
            snapshot,
            isReady: false,
            errorCode is null ? null : ServiceHealthErrorCode.EnsureValid(errorCode, nameof(errorCode)));
    }

    /// <summary>
    /// Creates the not-ready decision used when no snapshot could be obtained at all.
    /// </summary>
    /// <param name="errorCode">The stable safe code describing the bounded failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errorCode"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is not a safe code.</exception>
    public static ServiceReadinessDecision Unavailable(string errorCode)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        return new ServiceReadinessDecision(
            snapshot: null,
            isReady: false,
            ServiceHealthErrorCode.EnsureValid(errorCode, nameof(errorCode)));
    }

    /// <summary>Returns only the finite decision, safe error code, and snapshot projection.</summary>
    public override string ToString() =>
        $"ServiceReadinessDecision(IsReady={IsReady}, ErrorCode={ErrorCode ?? "<none>"}, " +
        $"Snapshot={Snapshot?.ToString() ?? "<none>"})";
}

/// <summary>
/// Supplies the single final readiness decision shared by every ServiceMantle surface.
/// </summary>
/// <remarks>
/// Implementations are read-only and cooperative: they perform no registration, background polling,
/// caching, or retry, and they propagate caller cancellation with the caller's own token. Each call
/// reads the base snapshot at most once and returns one complete immutable decision. Two calls made
/// at different moments may observe different state; what is shared is the source and the evaluation
/// algorithm, not the returned object.
/// </remarks>
public interface IServiceReadinessDecisionSource
{
    /// <summary>Evaluates one complete readiness decision for the current moment.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled the evaluation.</exception>
    ValueTask<ServiceReadinessDecision> GetDecisionAsync(
        CancellationToken cancellationToken = default);
}

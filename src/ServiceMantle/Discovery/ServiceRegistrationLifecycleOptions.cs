namespace ServiceMantle.Discovery;

/// <summary>
/// Carries the timing of a service registration lifecycle. Provider neutral data only.
/// </summary>
/// <remarks>
/// This type only holds values; it performs no validation and creates no timer. Validation and
/// the immutable runtime copy are owned by the provider adapter's registration entry (for
/// example <c>AddServiceMantleConsul</c>), which validates every value before any readiness
/// sampler, timer, or remote operation can exist.
/// </remarks>
public sealed class ServiceRegistrationLifecycleOptions
{
    /// <summary>Gets or sets the delay between completed readiness samples. 100 ms - 30 s.</summary>
    public TimeSpan ReadinessPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the outer budget for one decision-source call. 100 ms - 60 s.</summary>
    public TimeSpan ReadinessCallBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the budget for one register or deregister call. 100 ms - 30 s.</summary>
    public TimeSpan OperationBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the first transport retry delay. 50 ms - 5 s.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets the exponential delay ceiling. At least
    /// <see cref="InitialRetryDelay"/>, at most 30 s.
    /// </summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the total cooperative cleanup time after stop begins. 1 s - 60 s.</summary>
    /// <remarks>
    /// The budget starts when stop begins, before the owner loop is woken, and it bounds the cleanup
    /// deregistration: whatever an already in-flight operation spends settling is deducted from what
    /// the cleanup has left, and a cleanup retry delay is cut short by the remaining budget rather
    /// than by a fresh one. It does not shorten the operation that is already in flight, which keeps
    /// <see cref="OperationBudget"/> as its own cancellation deadline; an operation that has
    /// already run for part of that budget settles within whatever is left of it. For cooperative
    /// dependencies the resulting bound on one stop is therefore
    /// <c>max(OperationBudget, ShutdownBudget)</c> - with <c>OperationBudget = 30 s</c>
    /// and <c>ShutdownBudget = 1 s</c>, a stop can take about 30 seconds. It is not exact scheduling
    /// time, and it is no bound at all on a client, factory, or disposal that ignores cancellation.
    /// </remarks>
    public TimeSpan ShutdownBudget { get; set; } = TimeSpan.FromSeconds(15);
}

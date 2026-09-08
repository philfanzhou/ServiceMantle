namespace ServiceMantle.Consul;

/// <summary>The finite control state of the Consul registration lifecycle.</summary>
public enum ConsulLifecycleState
{
    /// <summary>The captured configuration is disabled; terminal until the process restarts.</summary>
    Disabled,

    /// <summary>Enabled, the latest decision is not Ready, and no operation is active.</summary>
    NotReady,

    /// <summary>One register operation is active.</summary>
    Registering,

    /// <summary>A register operation returned Success while the latest desire is present.</summary>
    Registered,

    /// <summary>One deregister operation is active.</summary>
    Deregistering,

    /// <summary>No remote operation is active and an intent-specific retry delay is running.</summary>
    Backoff,

    /// <summary>Stop has priority; no new register may start.</summary>
    Stopping,
}

/// <summary>
/// The conservative observation of whether this instance's record exists at the agent.
/// </summary>
public enum ConsulRemotePresence
{
    /// <summary>No register succeeded since startup, or the latest deregister returned Success.</summary>
    Absent,

    /// <summary>A register Success is not followed by a completed deregister Success.</summary>
    Present,

    /// <summary>An operation may have had a remote side effect. A register timeout lands here.</summary>
    Unknown,
}

/// <summary>
/// Configures the timing of the Consul registration lifecycle.
/// </summary>
/// <remarks>
/// Every value is validated when the capability is registered, which is before any readiness
/// sampler, timer, or remote operation exists. Invalid, non-finite, or conflicting values fail the
/// host before it starts.
/// </remarks>
public sealed class ConsulLifecycleOptions
{
    /// <summary>Gets or sets the delay between completed readiness samples. 100 ms - 30 s.</summary>
    public TimeSpan ReadinessPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the outer budget for one decision-source call. 100 ms - 60 s.</summary>
    public TimeSpan ReadinessCallBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the budget for one register or deregister call. 100 ms - 30 s.</summary>
    public TimeSpan ConsulOperationBudget { get; set; } = TimeSpan.FromSeconds(10);

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
    /// <see cref="ConsulOperationBudget"/> as its own cancellation deadline; an operation that has
    /// already run for part of that budget settles within whatever is left of it. For cooperative
    /// dependencies the resulting bound on one stop is therefore
    /// <c>max(ConsulOperationBudget, ShutdownBudget)</c> - with <c>ConsulOperationBudget = 30 s</c>
    /// and <c>ShutdownBudget = 1 s</c>, a stop can take about 30 seconds. It is not exact scheduling
    /// time, and it is no bound at all on a client, factory, or disposal that ignores cancellation.
    /// </remarks>
    public TimeSpan ShutdownBudget { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Returns a validated immutable copy, or throws before any work can start.</summary>
    /// <exception cref="ConsulConfigurationException">A value is out of range or conflicting.</exception>
    internal ConsulLifecycleSettings Validate()
    {
        Ensure(ReadinessPollInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        Ensure(ReadinessCallBudget, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60));
        Ensure(ConsulOperationBudget, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        Ensure(InitialRetryDelay, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        Ensure(MaximumRetryDelay, InitialRetryDelay, TimeSpan.FromSeconds(30));
        Ensure(ShutdownBudget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        return new ConsulLifecycleSettings(
            ReadinessPollInterval,
            ReadinessCallBudget,
            ConsulOperationBudget,
            InitialRetryDelay,
            MaximumRetryDelay,
            ShutdownBudget);
    }

    private static void Ensure(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        // TimeSpan cannot hold a non-finite value, but MinValue/MaxValue and any inverted range
        // are rejected here rather than turned into an unbounded wait.
        if (value < minimum || value > maximum || minimum > maximum)
        {
            throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
        }
    }
}

/// <summary>The validated immutable timing the controller actually runs on.</summary>
internal sealed record ConsulLifecycleSettings(
    TimeSpan ReadinessPollInterval,
    TimeSpan ReadinessCallBudget,
    TimeSpan ConsulOperationBudget,
    TimeSpan InitialRetryDelay,
    TimeSpan MaximumRetryDelay,
    TimeSpan ShutdownBudget)
{
    /// <summary>
    /// Returns the overflow-safe exponential delay <c>min(maximum, initial * 2^failures)</c>.
    /// There is deliberately no jitter.
    /// </summary>
    internal TimeSpan RetryDelay(int failures)
    {
        if (failures >= 62)
        {
            return MaximumRetryDelay;
        }

        var scaled = InitialRetryDelay.Ticks <= 0 || failures < 0
            ? MaximumRetryDelay.Ticks
            : InitialRetryDelay.Ticks > long.MaxValue >> failures
                ? MaximumRetryDelay.Ticks
                : InitialRetryDelay.Ticks << failures;
        return TimeSpan.FromTicks(Math.Min(scaled, MaximumRetryDelay.Ticks));
    }
}

/// <summary>The finite, value-free classifications the lifecycle records.</summary>
internal static class ConsulLifecycleDiagnostics
{
    internal const string ReadinessUnavailable = "readiness_unavailable";
    internal const string ReadinessTimeout = "readiness_timeout";
    internal const string RegisterRejected = "register_rejected";
    internal const string RegisterUnavailable = "register_unavailable";
    internal const string RegisterTimeout = "register_timeout";
    internal const string DeregisterRejected = "deregister_rejected";
    internal const string DeregisterUnavailable = "deregister_unavailable";
    internal const string DeregisterTimeout = "deregister_timeout";
    internal const string ShutdownTimeout = "shutdown_timeout";
    internal const string SessionDisposalFailed = "session_disposal_failed";
}

/// <summary>
/// One recorded lifecycle classification. It carries only finite metadata: never the ACL token, the
/// endpoint, the health URL, the address, the service name, the registration ID, a request or
/// response body, or a raw exception.
/// </summary>
internal sealed record ConsulLifecycleDiagnostic(
    string Classification,
    ConsulLifecycleState State,
    ConsulRemotePresence Presence,
    int Attempt,
    long SnapshotVersion)
{
    public override string ToString() =>
        $"ConsulLifecycleDiagnostic(Classification={Classification}, State={State}, " +
        $"Presence={Presence}, Attempt={Attempt}, SnapshotVersion={SnapshotVersion})";
}

/// <summary>
/// Retains the most recent lifecycle classifications in a fixed-capacity ring buffer.
/// </summary>
/// <remarks>
/// The lifecycle records on every failed readiness sample and every failed remote attempt, so a
/// long-lived process on a repeating failure path would otherwise grow this collection without
/// limit. Only <see cref="Capacity"/> entries are retained; older ones are overwritten.
/// <see cref="RecordedCount"/> keeps counting past that so a caller can still tell how often the
/// classification happened.
/// </remarks>
internal sealed class ConsulLifecycleObserver
{
    /// <summary>The number of most recent diagnostics retained.</summary>
    internal const int Capacity = 64;

    private readonly ConsulLifecycleDiagnostic?[] retained = new ConsulLifecycleDiagnostic?[Capacity];
    private readonly Lock gate = new();
    private long recorded;

    /// <summary>Gets the total number of diagnostics recorded, including those overwritten.</summary>
    internal long RecordedCount
    {
        get
        {
            lock (gate)
            {
                return recorded;
            }
        }
    }

    /// <summary>Gets the retained diagnostics, oldest retained first.</summary>
    internal IReadOnlyList<ConsulLifecycleDiagnostic> Diagnostics
    {
        get
        {
            lock (gate)
            {
                var count = (int)Math.Min(recorded, Capacity);
                var start = recorded <= Capacity ? 0 : (int)(recorded % Capacity);
                var snapshot = new ConsulLifecycleDiagnostic[count];
                for (var index = 0; index < count; index++)
                {
                    snapshot[index] = retained[(start + index) % Capacity]!;
                }

                return snapshot;
            }
        }
    }

    internal void Record(ConsulLifecycleDiagnostic diagnostic)
    {
        lock (gate)
        {
            retained[(int)(recorded % Capacity)] = diagnostic;
            recorded++;
        }
    }
}

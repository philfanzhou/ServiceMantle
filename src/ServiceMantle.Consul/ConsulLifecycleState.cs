using ServiceMantle.Discovery;

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
    /// Reads the six neutral timing properties, validates them, and returns the immutable copy the
    /// lifecycle runs on. Called by the Consul registration entry before any descriptor that could
    /// reach a timer is written, so an invalid value fails the registration call itself.
    /// </summary>
    /// <exception cref="ConsulConfigurationException">A value is out of range or conflicting.</exception>
    internal static ConsulLifecycleSettings FromOptions(ServiceRegistrationLifecycleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Ensure(options.ReadinessPollInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        Ensure(options.ReadinessCallBudget, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60));
        Ensure(options.OperationBudget, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        Ensure(options.InitialRetryDelay, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        Ensure(options.MaximumRetryDelay, options.InitialRetryDelay, TimeSpan.FromSeconds(30));
        Ensure(options.ShutdownBudget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        return new ConsulLifecycleSettings(
            options.ReadinessPollInterval,
            options.ReadinessCallBudget,
            options.OperationBudget,
            options.InitialRetryDelay,
            options.MaximumRetryDelay,
            options.ShutdownBudget);
    }

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

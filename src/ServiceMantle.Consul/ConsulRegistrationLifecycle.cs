using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Health;

namespace ServiceMantle.Consul;

/// <summary>
/// Drives the Consul registration of one instance from the shared final readiness decision.
/// </summary>
/// <remarks>
/// One owner loop holds every mutable field and the session, so at most one register or deregister
/// operation is active for this instance at any moment. A separate non-overlapping sampler publishes
/// the latest desire and never performs Consul work. Only a completed register Success makes the
/// remote presence Present, and only a completed deregister Success makes it Absent again; every
/// other outcome, a timeout included, is Unknown and therefore never claims remote absence.
/// </remarks>
internal sealed class ConsulRegistrationLifecycle : IHostedService, IAsyncDisposable
{
    private readonly ConsulClientProvider provider;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ConsulLifecycleSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly ConsulLifecycleObserver observer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim desireChanged = new(0, 1);

    private ConsulClientSession? session;
    private Task? sampler;
    private Task? owner;
    private int desiredPresent;
    private int sampled;
    private int stopping;
    private bool disposed;

    internal ConsulRegistrationLifecycle(
        ConsulClientProvider provider,
        IServiceScopeFactory scopeFactory,
        ConsulLifecycleSettings settings,
        TimeProvider timeProvider,
        ConsulLifecycleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(observer);

        this.provider = provider;
        this.scopeFactory = scopeFactory;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.observer = observer;
    }

    /// <summary>Gets the current finite control state.</summary>
    internal ConsulLifecycleState State { get; private set; } = ConsulLifecycleState.NotReady;

    /// <summary>Gets the conservative remote presence observation.</summary>
    internal ConsulRemotePresence Presence { get; private set; } = ConsulRemotePresence.Absent;

    /// <summary>Gets the captured snapshot version, or 0 while no session exists.</summary>
    internal long SnapshotVersion => session?.SnapshotVersion ?? 0;

    /// <summary>
    /// Resolves the session exactly once. A disabled configuration creates no client, sampler,
    /// timer, or loop; a configuration failure fails host startup without retrying.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = provider.CreateClient();
        if (created is null)
        {
            State = ConsulLifecycleState.Disabled;
            Presence = ConsulRemotePresence.Absent;
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The session was produced before cancellation was observed, so it is disposed here
            // rather than leaked; disposal is not deregistration.
            DisposeSession(created);
            cancellationToken.ThrowIfCancellationRequested();
        }

        session = created;
        State = ConsulLifecycleState.NotReady;
        Presence = ConsulRemotePresence.Absent;
        sampler = Task.Run(() => SampleLoopAsync(lifetime.Token), CancellationToken.None);
        owner = Task.Run(OwnLoopAsync, CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the sampler and every delay, settles any in-flight operation, then deregisters
    /// within the cooperative shutdown budget. It never starts another register.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref stopping, 1);
        Signal();
        await lifetime.CancelAsync().ConfigureAwait(false);

        if (owner is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        if (sampler is { } sample)
        {
            await sample.ConfigureAwait(false);
        }

        if (session is null)
        {
            return;
        }

        State = ConsulLifecycleState.Stopping;
        try
        {
            await CleanUpAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DisposeSession(session);
            session = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Interlocked.Exchange(ref stopping, 1);
        await lifetime.CancelAsync().ConfigureAwait(false);
        lifetime.Dispose();
        desireChanged.Dispose();
        if (session is { } owned)
        {
            DisposeSession(owned);
            session = null;
        }
    }

    /// <summary>
    /// Samples the shared readiness decision without overlapping. Every failure is fail-closed:
    /// the desire becomes absent rather than staying at its last value.
    /// </summary>
    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ready = await SampleAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var desired = ready ? 1 : 0;
            // Only an actual change - or the very first sample - wakes the owner loop, so a repeated
            // decision can never cut a retry delay short.
            if (Interlocked.Exchange(ref desiredPresent, desired) != desired ||
                Interlocked.Exchange(ref sampled, 1) == 0)
            {
                Signal();
            }

            try
            {
                await Task.Delay(settings.ReadinessPollInterval, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> SampleAsync(CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(settings.ReadinessCallBudget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            budget.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            // The default decision source is scoped, so exactly one is resolved per sample and the
            // scope lives until that sample settles.
            var source = scope.ServiceProvider.GetRequiredService<IServiceReadinessDecisionSource>();
            var decision = await source.GetDecisionAsync(linked.Token).ConfigureAwait(false);
            if (decision is null)
            {
                Record(ConsulLifecycleDiagnostics.ReadinessUnavailable);
                return false;
            }

            return decision.IsReady;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            // Scope creation, resolution, the call itself, and disposal all fail closed.
            Record(budget.IsCancellationRequested
                ? ConsulLifecycleDiagnostics.ReadinessTimeout
                : ConsulLifecycleDiagnostics.ReadinessUnavailable);
            return false;
        }
    }

    /// <summary>The single owner of every mutable field, the session, and all remote operations.</summary>
    private async Task OwnLoopAsync()
    {
        var failures = 0;
        var failedDesire = false;
        var backoffStartedAt = timeProvider.GetUtcNow();
        var backoff = false;

        while (Volatile.Read(ref stopping) == 0)
        {
            var desire = Volatile.Read(ref desiredPresent) == 1;
            if (backoff && desire != failedDesire)
            {
                // A change in desired presence cancels the delay and resets the failure count.
                backoff = false;
                failures = 0;
            }

            if (Settled(desire))
            {
                State = desire ? ConsulLifecycleState.Registered : ConsulLifecycleState.NotReady;
                failures = 0;
                backoff = false;
                await WaitForDesireAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                continue;
            }

            if (backoff)
            {
                State = ConsulLifecycleState.Backoff;
                var remaining = backoffStartedAt + settings.RetryDelay(failures - 1)
                    - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    await WaitForDesireAsync(remaining).ConfigureAwait(false);
                    // The loop re-reads the desire and the remaining delay; a wake-up that is not
                    // the delay itself never shortens it.
                    continue;
                }

                backoff = false;
                continue;
            }

            if (await RunOperationAsync(desire, failures).ConfigureAwait(false))
            {
                failures = 0;
                continue;
            }

            failures++;
            failedDesire = desire;
            backoffStartedAt = timeProvider.GetUtcNow();
            backoff = true;
        }
    }

    private bool Settled(bool desire) => desire
        ? Presence == ConsulRemotePresence.Present
        : Presence == ConsulRemotePresence.Absent;

    /// <summary>
    /// Runs exactly one remote operation. A desire flip or a stop requests cancellation of that
    /// attempt and then waits for it to settle, so a late completion can never overlap the next one.
    /// </summary>
    private async Task<bool> RunOperationAsync(bool register, int failures)
    {
        var owned = session;
        if (owned is null)
        {
            return false;
        }

        State = register ? ConsulLifecycleState.Registering : ConsulLifecycleState.Deregistering;
        using var budget = new CancellationTokenSource(settings.ConsulOperationBudget, timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var call = (register
            ? owned.RegisterAsync(operation.Token)
            : owned.DeregisterAsync(operation.Token)).AsTask();

        while (!call.IsCompleted)
        {
            var interrupted = await WaitForDesireAsync(Timeout.InfiniteTimeSpan, call)
                .ConfigureAwait(false);
            if (call.IsCompleted)
            {
                break;
            }

            if (!interrupted)
            {
                continue;
            }

            if (Volatile.Read(ref stopping) != 0 ||
                (Volatile.Read(ref desiredPresent) == 1) != register)
            {
                await operation.CancelAsync().ConfigureAwait(false);
                break;
            }
        }

        ConsulClientResult result;
        try
        {
            result = await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The attempt settled cooperatively. Its remote effect is unknown either way.
            Presence = ConsulRemotePresence.Unknown;
            Record(
                budget.IsCancellationRequested
                    ? register
                        ? ConsulLifecycleDiagnostics.RegisterTimeout
                        : ConsulLifecycleDiagnostics.DeregisterTimeout
                    : register
                        ? ConsulLifecycleDiagnostics.RegisterUnavailable
                        : ConsulLifecycleDiagnostics.DeregisterUnavailable,
                failures);
            return false;
        }
        catch
        {
            Presence = ConsulRemotePresence.Unknown;
            Record(
                register
                    ? ConsulLifecycleDiagnostics.RegisterUnavailable
                    : ConsulLifecycleDiagnostics.DeregisterUnavailable,
                failures);
            return false;
        }

        if (result == ConsulClientResult.Success)
        {
            Presence = register ? ConsulRemotePresence.Present : ConsulRemotePresence.Absent;
            return true;
        }

        Presence = ConsulRemotePresence.Unknown;
        Record(
            result == ConsulClientResult.Rejected
                ? register
                    ? ConsulLifecycleDiagnostics.RegisterRejected
                    : ConsulLifecycleDiagnostics.DeregisterRejected
                : register
                    ? ConsulLifecycleDiagnostics.RegisterUnavailable
                    : ConsulLifecycleDiagnostics.DeregisterUnavailable,
            failures);
        return false;
    }

    /// <summary>
    /// Deregisters within the remaining shutdown budget. It never claims absence it did not
    /// observe, and it starts no new register.
    /// </summary>
    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        if (Presence == ConsulRemotePresence.Absent)
        {
            return;
        }

        Interlocked.Exchange(ref desiredPresent, 0);
        using var shutdown = new CancellationTokenSource(settings.ShutdownBudget, timeProvider);
        var failures = 0;
        while (!shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var owned = session;
            if (owned is null)
            {
                return;
            }

            using var budget = new CancellationTokenSource(settings.ConsulOperationBudget, timeProvider);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                budget.Token,
                shutdown.Token);
            try
            {
                var result = await owned.DeregisterAsync(operation.Token).ConfigureAwait(false);
                if (result == ConsulClientResult.Success)
                {
                    Presence = ConsulRemotePresence.Absent;
                    return;
                }

                Presence = ConsulRemotePresence.Unknown;
                Record(
                    result == ConsulClientResult.Rejected
                        ? ConsulLifecycleDiagnostics.DeregisterRejected
                        : ConsulLifecycleDiagnostics.DeregisterUnavailable,
                    failures);
            }
            catch (OperationCanceledException)
            {
                Presence = ConsulRemotePresence.Unknown;
                Record(
                    budget.IsCancellationRequested
                        ? ConsulLifecycleDiagnostics.DeregisterTimeout
                        : ConsulLifecycleDiagnostics.DeregisterUnavailable,
                    failures);
            }
            catch
            {
                Presence = ConsulRemotePresence.Unknown;
                Record(ConsulLifecycleDiagnostics.DeregisterUnavailable, failures);
            }

            failures++;
            try
            {
                await Task.Delay(settings.RetryDelay(failures - 1), timeProvider, shutdown.Token)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The stop caller's own cancellation stays the caller's; no remote absence is claimed.
            return;
        }

        Record(ConsulLifecycleDiagnostics.ShutdownTimeout, failures);
    }

    /// <summary>
    /// Waits for the next desire signal, an optional delay, an in-flight call, or the lifetime
    /// token, and reports whether a desire signal is what woke it.
    /// </summary>
    private async Task<bool> WaitForDesireAsync(TimeSpan delay, Task? call = null)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var signal = desireChanged.WaitAsync(wait.Token);
        var others = new List<Task>(3) { signal };
        if (call is not null)
        {
            others.Add(call);
        }

        if (delay != Timeout.InfiniteTimeSpan)
        {
            others.Add(Task.Delay(delay, timeProvider, wait.Token));
        }

        var completed = await Task.WhenAny(others).ConfigureAwait(false);
        var signalled = completed == signal && signal.IsCompletedSuccessfully;
        await wait.CancelAsync().ConfigureAwait(false);
        foreach (var other in others)
        {
            if (other == call)
            {
                continue;
            }

            try
            {
                await other.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The losing waiters are cancelled on purpose.
            }
        }

        return signalled;
    }

    private void Signal()
    {
        try
        {
            if (desireChanged.CurrentCount == 0)
            {
                desireChanged.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // The owner loop has not consumed the previous signal yet; one pending wake is enough.
        }
        catch (ObjectDisposedException)
        {
            // The lifecycle is already disposed.
        }
    }

    private void DisposeSession(ConsulClientSession owned)
    {
        try
        {
            owned.Dispose();
        }
        catch (ConsulConfigurationException)
        {
            // Disposal is never retried and never implies that the service was deregistered.
            Record(ConsulLifecycleDiagnostics.SessionDisposalFailed);
        }
    }

    private void Record(string classification, int attempt = 0) => observer.Record(
        new ConsulLifecycleDiagnostic(classification, State, Presence, attempt, SnapshotVersion));
}

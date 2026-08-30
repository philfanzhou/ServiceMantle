namespace ServiceMantle.Testing;

/// <summary>
/// Coordinates exactly two in-process test actors at a single rendezvous point.
/// </summary>
public sealed class TwoActorBarrier
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1D);
    private readonly TaskCompletionSource<TwoActorBarrierTimeoutException?> rendezvous =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan timeout;
    private int arrivals;
    private int firstActorArrived;
    private int secondActorArrived;

    public TwoActorBarrier(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.timeout = timeout;
    }

    public ValueTask FirstActorAsync(CancellationToken cancellationToken) =>
        SignalAndWaitAsync(firstActor: true, cancellationToken);

    public ValueTask SecondActorAsync(CancellationToken cancellationToken) =>
        SignalAndWaitAsync(firstActor: false, cancellationToken);

    private async ValueTask SignalAndWaitAsync(bool firstActor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ref var actorArrival = ref firstActor ? ref firstActorArrived : ref secondActorArrived;
        if (Interlocked.Exchange(ref actorArrival, 1) != 0)
        {
            throw new InvalidOperationException("A two-actor barrier participant arrived more than once.");
        }

        if (Interlocked.Increment(ref arrivals) == 2)
        {
            rendezvous.TrySetResult(null);
        }

        try
        {
            var failure = await rendezvous.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
        catch (TimeoutException exception) when (exception is not TwoActorBarrierTimeoutException)
        {
            var failure = new TwoActorBarrierTimeoutException(timeout, Volatile.Read(ref arrivals));
            if (rendezvous.TrySetResult(failure))
            {
                throw failure;
            }

            var concurrentFailure = await rendezvous.Task.ConfigureAwait(false);
            if (concurrentFailure is not null)
            {
                throw concurrentFailure;
            }
        }
    }
}

public sealed class TwoActorBarrierTimeoutException : TimeoutException
{
    internal TwoActorBarrierTimeoutException(TimeSpan timeout, int arrivedActorCount)
        : base(
            $"The two-actor barrier timed out after {timeout.TotalMilliseconds:F0} ms " +
            $"with {arrivedActorCount} of 2 actors present.")
    {
        Timeout = timeout;
        ArrivedActorCount = arrivedActorCount;
    }

    public TimeSpan Timeout { get; }

    public int ArrivedActorCount { get; }
}

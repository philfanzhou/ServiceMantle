namespace ServiceMantle.Consul.Tests;

/// <summary>
/// Minimal advanceable <see cref="TimeProvider"/>. Only the members the lifecycle touches are
/// implemented: the clock, the timestamp pair, and timers driven by <see cref="Advance"/> instead of
/// by the wall clock.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private readonly List<(DateTimeOffset DueAt, TaskCompletionSource Completion)> waiters = [];
    private DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return utcNow;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return utcNow.UtcTicks;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            Schedule(timer, dueTime, period);
            timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    /// Completes once some timer is armed for exactly <paramref name="dueAt"/>. This replaces
    /// waiting on the wall clock for a delay to be scheduled before the clock is advanced past it.
    /// </summary>
    public Task WhenTimerScheduledAsync(DateTimeOffset dueAt)
    {
        lock (gate)
        {
            if (timers.Any(timer => timer.DueAt == dueAt))
            {
                return Task.CompletedTask;
            }

            var waiter = (dueAt, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            waiters.Add(waiter);
            return waiter.Item2.Task;
        }
    }

    /// <summary>Moves the virtual clock forward and fires every timer that becomes due.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (gate)
        {
            utcNow += delta;
        }

        while (true)
        {
            ManualTimer? due;
            lock (gate)
            {
                due = TakeDueTimer();
            }

            if (due is null)
            {
                return;
            }

            due.Invoke();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        timer.Period = period;
        timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : utcNow + dueTime;
        for (var index = waiters.Count - 1; index >= 0; index--)
        {
            if (waiters[index].DueAt != timer.DueAt)
            {
                continue;
            }

            waiters[index].Completion.SetResult();
            waiters.RemoveAt(index);
        }
    }

    private ManualTimer? TakeDueTimer()
    {
        foreach (var timer in timers)
        {
            if (timer.DueAt is not { } dueAt || dueAt > utcNow)
            {
                continue;
            }

            timer.DueAt = timer.Period <= TimeSpan.Zero || timer.Period == Timeout.InfiniteTimeSpan
                ? null
                : utcNow + timer.Period;
            return timer;
        }

        return null;
    }

    private void ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            Schedule(timer, dueTime, period);
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (gate)
        {
            timer.DueAt = null;
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state)
        : ITimer
    {
        internal DateTimeOffset? DueAt { get; set; }

        internal TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            provider.ChangeTimer(this, dueTime, period);
            return true;
        }

        public void Dispose() => provider.RemoveTimer(this);

        public ValueTask DisposeAsync()
        {
            provider.RemoveTimer(this);
            return ValueTask.CompletedTask;
        }

        internal void Invoke() => callback(state);
    }
}

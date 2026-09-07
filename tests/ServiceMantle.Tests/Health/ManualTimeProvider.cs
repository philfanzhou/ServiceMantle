namespace ServiceMantle.Tests.Health;

/// <summary>
/// Minimal advanceable <see cref="TimeProvider"/> used to pin how the shared contributor budget is
/// divided. Only the members the combiner touches are implemented: the timestamp pair that measures
/// elapsed budget, and timers driven by <see cref="Advance"/> instead of by the wall clock.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private readonly List<TimerCountWaiter> timerWaiters = [];
    private DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private int createdTimerCount;

    /// <summary>Gets the number of timers created so far, including disposed ones.</summary>
    public int CreatedTimerCount
    {
        get
        {
            lock (gate)
            {
                return createdTimerCount;
            }
        }
    }

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
        List<TimerCountWaiter> satisfied;
        lock (gate)
        {
            Schedule(timer, dueTime, period);
            timers.Add(timer);
            createdTimerCount++;
            satisfied = TakeSatisfiedWaiters();
        }

        Release(satisfied);
        return timer;
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

    /// <summary>
    /// Completes once at least <paramref name="count"/> timers have been created and armed.
    /// The combiner arms a timer for each contributor deadline, so this replaces waiting on the
    /// wall clock for the combiner to reach the next contributor.
    /// </summary>
    public Task WhenTimerCountAtLeastAsync(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            if (createdTimerCount >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new TimerCountWaiter(
                count,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            timerWaiters.Add(waiter);
            return waiter.Completion.Task;
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        timer.Period = period;
        timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : utcNow + dueTime;
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

    private List<TimerCountWaiter> TakeSatisfiedWaiters()
    {
        var satisfied = new List<TimerCountWaiter>();
        for (var index = timerWaiters.Count - 1; index >= 0; index--)
        {
            if (timerWaiters[index].Count > createdTimerCount)
            {
                continue;
            }

            satisfied.Add(timerWaiters[index]);
            timerWaiters.RemoveAt(index);
        }

        return satisfied;
    }

    private static void Release(List<TimerCountWaiter> satisfied)
    {
        foreach (var waiter in satisfied)
        {
            waiter.Completion.SetResult();
        }
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

    private sealed record TimerCountWaiter(int Count, TaskCompletionSource Completion);

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

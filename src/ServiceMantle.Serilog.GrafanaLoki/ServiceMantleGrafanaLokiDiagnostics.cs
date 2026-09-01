namespace ServiceMantle.Serilog.GrafanaLoki;

/// <summary>Exposes content-free counters for bounded Loki delivery diagnostics.</summary>
public sealed class ServiceMantleGrafanaLokiDiagnostics
{
    private long failedBatchCount;
    private long droppedEventCount;
    private long drainTimeoutCount;
    private long drainCancellationCount;
    private string? lastErrorCode;

    /// <summary>Gets the number of failed batch attempts reported by Serilog.</summary>
    public long FailedBatchCount => Interlocked.Read(ref failedBatchCount);

    /// <summary>Gets the number of accepted events not acknowledged by a successful post before shutdown.</summary>
    public long DroppedEventCount => Interlocked.Read(ref droppedEventCount);

    /// <summary>Gets the number of shutdown drains that reached the configured timeout.</summary>
    public long DrainTimeoutCount => Interlocked.Read(ref drainTimeoutCount);

    /// <summary>Gets the number of shutdown drains cancelled by the Host token.</summary>
    public long DrainCancellationCount => Interlocked.Read(ref drainCancellationCount);

    /// <summary>Gets the last stable failure classification, when present.</summary>
    public string? LastErrorCode => Volatile.Read(ref lastErrorCode);

    internal void RecordFailedBatch(string errorCode)
    {
        Interlocked.Increment(ref failedBatchCount);
        Volatile.Write(ref lastErrorCode, errorCode);
    }

    internal void RecordDroppedEvents(long count)
    {
        while (count > 0)
        {
            var current = Interlocked.Read(ref droppedEventCount);
            if (current >= count || Interlocked.CompareExchange(ref droppedEventCount, count, current) == current)
            {
                return;
            }
        }
    }

    internal void RecordDrainTimeout()
    {
        Interlocked.Increment(ref drainTimeoutCount);
        Volatile.Write(ref lastErrorCode, WellKnownServiceMantleGrafanaLokiErrorCodes.ShutdownDrainTimedOut);
    }

    internal void RecordDrainCancellation()
    {
        Interlocked.Increment(ref drainCancellationCount);
        Volatile.Write(ref lastErrorCode, WellKnownServiceMantleGrafanaLokiErrorCodes.ShutdownDrainCancelled);
    }

    /// <summary>Returns only bounded counters and stable classifications.</summary>
    public override string ToString() =>
        $"ServiceMantleGrafanaLokiDiagnostics(FailedBatchCount={FailedBatchCount}, " +
        $"DroppedEventCount={DroppedEventCount}, DrainTimeoutCount={DrainTimeoutCount}, " +
        $"DrainCancellationCount={DrainCancellationCount}, LastErrorCode={LastErrorCode ?? "none"})";
}

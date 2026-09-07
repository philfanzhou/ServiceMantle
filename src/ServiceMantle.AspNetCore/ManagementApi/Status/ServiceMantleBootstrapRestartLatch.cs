namespace ServiceMantle.AspNetCore;

/// <summary>
/// The process-local latch that records whether this process wrote local Bootstrap configuration
/// that only a restart activates.
/// </summary>
/// <remarks>
/// The latch starts false, is set only after this process successfully creates or replaces its own
/// Bootstrap file, and resets when the process restarts because it is never persisted. It states
/// nothing about another instance, about a restart that already happened elsewhere, or about
/// configuration having been activated anywhere.
/// </remarks>
internal sealed class ServiceMantleBootstrapRestartLatch
{
    private int latched;

    /// <summary>
    /// Gets a value indicating whether this process wrote Bootstrap configuration that a restart
    /// still has to activate.
    /// </summary>
    internal bool RestartRequired => Volatile.Read(ref latched) != 0;

    /// <summary>Latches this process as requiring a restart; the latch never clears again.</summary>
    internal void Latch() => Volatile.Write(ref latched, 1);
}

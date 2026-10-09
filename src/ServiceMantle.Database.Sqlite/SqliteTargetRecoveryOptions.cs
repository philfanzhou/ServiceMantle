namespace ServiceMantle.Database.Sqlite;

/// <summary>
/// Explicit, opt-in single-checkpoint handling of existing local WAL targets: recovery of safe
/// WAL sidecars, and admission of a clean, sidecar-free WAL target.
/// </summary>
/// <remarks>
/// Enabling this option is the caller's informed declaration that the target is not concurrently
/// held by another process: one checkpoint attempt using an owned read-write connection may write
/// to the target and may transiently create WAL sidecars during the admission window. Without the
/// option, every clean WAL target keeps the default fail-closed refusal.
/// </remarks>
public sealed class SqliteTargetRecoveryOptions
{
    /// <summary>Initializes recovery settings. Recovery is disabled by default.</summary>
    public SqliteTargetRecoveryOptions(bool enabled = false, TimeSpan? recoveryTimeout = null)
    {
        var timeout = recoveryTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1D))
            throw new ArgumentOutOfRangeException(nameof(recoveryTimeout), "Recovery timeout must be positive and timer-supported.");
        Enabled = enabled;
        RecoveryTimeout = timeout;
    }

    /// <summary>
    /// Gets whether one WAL checkpoint attempt is allowed on a safely validated existing target,
    /// including admission of a clean, sidecar-free WAL target.
    /// </summary>
    public bool Enabled { get; }
    /// <summary>Gets the recovery time budget. SQLite busy waits have whole-second granularity.</summary>
    public TimeSpan RecoveryTimeout { get; }
}

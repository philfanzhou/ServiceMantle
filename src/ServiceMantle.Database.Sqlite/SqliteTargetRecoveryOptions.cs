namespace ServiceMantle.Database.Sqlite;

/// <summary>Explicit, opt-in recovery of safe SQLite WAL sidecars on existing local targets.</summary>
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

    /// <summary>Gets whether one WAL checkpoint attempt is allowed on a safely validated existing target.</summary>
    public bool Enabled { get; }
    /// <summary>Gets the recovery time budget. SQLite busy waits have whole-second granularity.</summary>
    public TimeSpan RecoveryTimeout { get; }
}

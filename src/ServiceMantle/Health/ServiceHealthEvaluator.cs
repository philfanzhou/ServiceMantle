using ServiceMantle.Installation;

namespace ServiceMantle.Health;

/// <summary>The deterministic live/readiness evaluation of one immutable snapshot.</summary>
public sealed class ServiceHealthEvaluation
{
    internal ServiceHealthEvaluation(ServiceHealthSnapshot snapshot, bool isReady)
    {
        Snapshot = snapshot;
        IsReady = isReady;
    }

    /// <summary>Gets the exact snapshot used for this evaluation.</summary>
    public ServiceHealthSnapshot Snapshot { get; }

    /// <summary>
    /// Gets whether the process is live. An executing health endpoint is always live.
    /// </summary>
    public bool IsLive => true;

    /// <summary>Gets whether the service is ready for normal traffic.</summary>
    public bool IsReady { get; }

    /// <summary>Returns only finite evaluation state and the safe snapshot projection.</summary>
    public override string ToString() =>
        $"ServiceHealthEvaluation(IsLive={IsLive}, IsReady={IsReady}, Snapshot={Snapshot})";
}

/// <summary>Evaluates the finite startup, migration, and database readiness matrix.</summary>
public static class ServiceHealthEvaluator
{
    /// <summary>Evaluates one immutable snapshot without I/O or shared mutable state.</summary>
    public static ServiceHealthEvaluation Evaluate(ServiceHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var isReady = snapshot.Phase == ServiceStartupPhase.Completed &&
            snapshot.MigrationStatus == ServiceMigrationReadinessState.Succeeded &&
            snapshot.DatabaseStatus == ServiceDatabaseReadinessState.Reachable;
        return new ServiceHealthEvaluation(snapshot, isReady);
    }
}

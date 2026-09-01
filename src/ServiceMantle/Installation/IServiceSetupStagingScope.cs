namespace ServiceMantle.Installation;

/// <summary>
/// Exposes the minimum caller-owned unit-of-work surface required by setup orchestration.
/// </summary>
public interface IServiceSetupStagingScope
{
    /// <summary>Gets whether the shared unit of work currently has pending changes.</summary>
    bool HasPendingChanges { get; }

    /// <summary>Discards all pending changes in the shared unit of work.</summary>
    ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default);
}

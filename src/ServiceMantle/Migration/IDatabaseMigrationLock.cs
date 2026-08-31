namespace ServiceMantle.Migration;

/// <summary>
/// Represents an acquired migration lock lease. The lock is held for the lifetime of this object.
/// </summary>
public interface IDatabaseMigrationLock : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider-specific lock identifier.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets a signal that is cancelled when the provider detects that this acquired lease is no
    /// longer held. Explicit disposal must not cancel this signal.
    /// </summary>
    /// <remarks>
    /// Detection is provider-specific and may be delayed by its probe interval and I/O timeout.
    /// Consumers must treat cancellation as permanent and stop authority-dependent work promptly.
    /// </remarks>
    CancellationToken LeaseLost { get; }
}

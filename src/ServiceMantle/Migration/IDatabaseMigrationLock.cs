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
}

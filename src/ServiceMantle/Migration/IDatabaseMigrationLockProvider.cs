namespace ServiceMantle.Migration;

/// <summary>
/// Provides optional multi-instance migration coordination for a database provider.
/// </summary>
public interface IDatabaseMigrationLockProvider
{
    /// <summary>
    /// Gets the provider identifier that this lock provider supports.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Attempts to acquire a migration lock based on the service identifier and bootstrap configuration.
    /// </summary>
    /// <param name="serviceId">The service identifier for which to acquire a lock.</param>
    /// <param name="bootstrap">The bootstrap configuration for the target database.</param>
    /// <param name="acquireTimeout">The maximum time to wait for lock acquisition. Cannot be infinite.</param>
    /// <param name="cancellationToken">Cancellation token. Cancellation takes precedence over timeout.</param>
    /// <returns>An acquired lock lease that must be disposed to release the lock.</returns>
    /// <exception cref="OperationCanceledException">Lock acquisition was cancelled by the caller.</exception>
    /// <exception cref="DatabaseMigrationLockException">
    /// Lock acquisition failed due to timeout, provider error, or unsupported capability.
    /// </exception>
    ValueTask<IDatabaseMigrationLock> AcquireAsync(
        ServiceId serviceId,
        Bootstrap.BootstrapDatabaseConfiguration bootstrap,
        TimeSpan acquireTimeout,
        CancellationToken cancellationToken = default);
}

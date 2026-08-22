namespace ServiceMantle.Installation;

/// <summary>
/// Provides durable installation state operations for a service.
/// </summary>
public interface IServiceInstallationStore
{
    /// <summary>
    /// Finds installation state for a service.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ServiceInstallationState?> FindAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a pending setup record exists and returns the current installation state.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ServiceInstallationState> CreatePendingAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a service installation as completed and returns the updated installation state.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ServiceInstallationState> MarkCompletedAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);
}


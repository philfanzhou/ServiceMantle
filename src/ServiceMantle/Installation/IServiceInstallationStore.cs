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
    /// Returns the state of an installation that is already completed, and refuses to complete a
    /// pending one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This entry point does not complete a pending installation. A pending row stably throws
    /// <see cref="ServiceInstallationStoreException"/> with
    /// <see cref="WellKnownSetupCodeErrorCodes.SetupCodeRequired"/>, because completing a pending
    /// installation must go through <see cref="IServiceSetupCodeStore.StageConsumeAsync"/> so that
    /// the Setup Code is actually validated and consumed in the caller's own unit of work.
    /// </para>
    /// <para>
    /// An already completed row stays an idempotent read and returns its completed state. A missing
    /// row throws <see cref="ServiceInstallationStoreException"/> with
    /// <see cref="WellKnownSetupCodeErrorCodes.InstallationNotFound"/>.
    /// </para>
    /// </remarks>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ServiceInstallationStoreException">
    /// The installation does not exist, its stored state is invalid, or it is still pending and
    /// therefore requires Setup Code consumption.
    /// </exception>
    ValueTask<ServiceInstallationState> MarkCompletedAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);
}


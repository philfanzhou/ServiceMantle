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
    /// Ensures a pending setup record exists, saves it when absent, and returns the current
    /// installation state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a standalone write that calls <c>SaveChangesAsync</c> once when the row is absent. It
    /// never creates, commits, or takes over a database transaction. It requires a clean DbContext:
    /// any pre-existing <c>Added</c>, <c>Modified</c>, or <c>Deleted</c> entry makes it throw
    /// <see cref="ServiceInstallationStoreException"/> with
    /// <see cref="WellKnownSetupCodeErrorCodes.DirtyContext"/> without saving. Unrelated
    /// <c>Unchanged</c> entries are allowed. A short-lived dedicated DbContext is the recommended
    /// shape.
    /// </para>
    /// <para>
    /// When the caller runs this inside a larger external transaction, success only means the save
    /// joined that transaction; ServiceMantle does not own or guarantee the caller's commit.
    /// </para>
    /// </remarks>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ServiceInstallationStoreException">
    /// The DbContext carries pending changes, or the installation row could not be stored.
    /// </exception>
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

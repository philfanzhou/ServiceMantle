using ServiceMantle;

namespace ServiceMantle.Installation;

/// <summary>
/// Represents the immutable installation state for a service.
/// </summary>
public sealed record ServiceInstallationState
{
    /// <summary>
    /// Gets the service to which this installation state belongs.
    /// </summary>
    public ServiceId ServiceId { get; }

    /// <summary>
    /// Gets the persisted installation status.
    /// </summary>
    public InstallationStatus Status { get; }

    /// <summary>
    /// Gets a value indicating whether initial setup has completed.
    /// </summary>
    public bool IsCompleted => Status == InstallationStatus.Completed;

    private ServiceInstallationState(ServiceId serviceId, InstallationStatus status)
    {
        ServiceId = serviceId;
        Status = status;
    }

    /// <summary>
    /// Creates a pending installation state for a service.
    /// </summary>
    /// <param name="serviceId">The service deployment identifier.</param>
    /// <returns>A pending installation state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    public static ServiceInstallationState CreatePending(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        return new ServiceInstallationState(serviceId, InstallationStatus.PendingSetup);
    }

    /// <summary>
    /// Returns a new state representing completed initial setup.
    /// </summary>
    /// <returns>A completed installation state.</returns>
    /// <exception cref="InvalidOperationException">The state is already completed.</exception>
    public ServiceInstallationState Complete()
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("The installation is already completed.");
        }

        return new ServiceInstallationState(ServiceId, InstallationStatus.Completed);
    }
}

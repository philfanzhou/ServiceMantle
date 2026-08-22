namespace ServiceMantle.Installation;

/// <summary>
/// Describes the persisted installation status of a service.
/// </summary>
public enum InstallationStatus
{
    /// <summary>
    /// Initial setup has not completed.
    /// </summary>
    PendingSetup = 0,

    /// <summary>
    /// Initial setup has completed.
    /// </summary>
    Completed = 1
}

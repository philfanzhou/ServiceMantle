namespace ServiceMantle.Installation;

/// <summary>
/// Describes the runtime phase reached while starting a service.
/// </summary>
public enum ServiceStartupPhase
{
    /// <summary>
    /// The service does not yet have valid business database bootstrap configuration.
    /// </summary>
    BootstrapConfiguration = 0,

    /// <summary>
    /// Database bootstrap conditions are available, but initial setup is incomplete.
    /// </summary>
    PendingSetup = 1,

    /// <summary>
    /// Initial setup is complete and normal service operation can begin.
    /// </summary>
    Completed = 2
}

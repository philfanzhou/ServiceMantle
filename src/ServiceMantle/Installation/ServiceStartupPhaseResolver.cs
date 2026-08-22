namespace ServiceMantle.Installation;

/// <summary>
/// Resolves the runtime startup phase from bootstrap availability and installation state.
/// </summary>
public static class ServiceStartupPhaseResolver
{
    /// <summary>
    /// Resolves the current startup phase.
    /// </summary>
    /// <param name="hasBootstrapConfiguration">Whether valid business database bootstrap configuration is available.</param>
    /// <param name="installationState">The successfully read installation state, if one exists.</param>
    /// <returns>The resolved runtime startup phase.</returns>
    public static ServiceStartupPhase Resolve(
        bool hasBootstrapConfiguration,
        ServiceInstallationState? installationState)
    {
        if (!hasBootstrapConfiguration)
        {
            return ServiceStartupPhase.BootstrapConfiguration;
        }

        if (installationState is null)
        {
            return ServiceStartupPhase.PendingSetup;
        }

        return installationState.Status switch
        {
            InstallationStatus.PendingSetup => ServiceStartupPhase.PendingSetup,
            InstallationStatus.Completed => ServiceStartupPhase.Completed,
            _ => throw new InvalidOperationException("The installation state has an unknown status.")
        };
    }
}

using ServiceMantle.Bootstrap;

namespace ServiceMantle.Migration;

/// <summary>
/// The immutable inputs of one <see cref="StartupDatabaseGate"/> run.
/// </summary>
/// <remarks>
/// Target preparation is an explicit switch: when
/// <see cref="EnableTargetPreparation"/> is false, no
/// <see cref="IDatabaseTargetPreparationProvider"/> is ever called. When it is true, creating a
/// missing target must be separately and explicitly permitted through
/// <see cref="AllowTargetCreation"/>; the library default is to refuse creation. Configuration is
/// never read from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>: the caller
/// reads its own configuration and supplies the values here.
/// </remarks>
public sealed class StartupDatabaseGateOptions
{
    /// <summary>Initializes the gate inputs and validates them eagerly.</summary>
    /// <param name="database">The target database configuration.</param>
    /// <param name="deploymentMode">The explicitly chosen deployment mode.</param>
    /// <param name="lockWaitBudget">
    /// The budget for waiting on the migration lock or the single-instance turn. It bounds the
    /// wait, never the execution.
    /// </param>
    /// <param name="enableTargetPreparation">
    /// Whether the optional target preparation provider runs before migration orchestration.
    /// </param>
    /// <param name="allowTargetCreation">
    /// Whether a missing target may be created. Only meaningful with target preparation enabled;
    /// the library default is to refuse creation.
    /// </param>
    /// <param name="maintenanceConnectionString">
    /// The administrative connection string used only for a preparation call that creates the
    /// target, or null for providers whose preparation needs no administrative connection.
    /// </param>
    /// <param name="preparationTimeout">The budget for one target preparation call.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A value is invalid for its use.</exception>
    public StartupDatabaseGateOptions(
        BootstrapDatabaseConfiguration database,
        DatabaseDeploymentMode deploymentMode,
        TimeSpan lockWaitBudget,
        bool enableTargetPreparation = false,
        bool allowTargetCreation = false,
        string? maintenanceConnectionString = null,
        TimeSpan? preparationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!Enum.IsDefined(deploymentMode) || deploymentMode == DatabaseDeploymentMode.Unspecified)
        {
            throw new ArgumentException(
                "The deployment mode must be explicitly SingleInstance or MultiInstance.",
                nameof(deploymentMode));
        }

        if (lockWaitBudget <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The lock wait budget must be positive.",
                nameof(lockWaitBudget));
        }

        if (allowTargetCreation && !enableTargetPreparation)
        {
            throw new ArgumentException(
                "Target creation may only be permitted together with target preparation.",
                nameof(allowTargetCreation));
        }

        var resolvedPreparationTimeout = preparationTimeout ?? TimeSpan.FromSeconds(30);
        if (resolvedPreparationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The preparation timeout must be positive.",
                nameof(preparationTimeout));
        }

        if (maintenanceConnectionString is not null)
        {
            maintenanceConnectionString = maintenanceConnectionString.Trim();
            if (maintenanceConnectionString.Length == 0)
            {
                throw new ArgumentException(
                    "The maintenance connection string cannot be whitespace.",
                    nameof(maintenanceConnectionString));
            }
        }

        Database = database;
        DeploymentMode = deploymentMode;
        LockWaitBudget = lockWaitBudget;
        EnableTargetPreparation = enableTargetPreparation;
        AllowTargetCreation = allowTargetCreation;
        MaintenanceConnectionString = maintenanceConnectionString;
        PreparationTimeout = resolvedPreparationTimeout;
    }

    /// <summary>Gets the target database configuration.</summary>
    public BootstrapDatabaseConfiguration Database { get; }

    /// <summary>Gets the explicitly chosen deployment mode.</summary>
    public DatabaseDeploymentMode DeploymentMode { get; }

    /// <summary>Gets the budget for waiting on the migration lock or single-instance turn.</summary>
    public TimeSpan LockWaitBudget { get; }

    /// <summary>Gets whether the optional target preparation provider runs.</summary>
    public bool EnableTargetPreparation { get; }

    /// <summary>Gets whether a missing target may be created.</summary>
    public bool AllowTargetCreation { get; }

    /// <summary>
    /// Gets the administrative connection string for a creating preparation call, or null when
    /// the provider needs none. It is used only for the duration of that call.
    /// </summary>
    public string? MaintenanceConnectionString { get; }

    /// <summary>Gets the budget for one target preparation call.</summary>
    public TimeSpan PreparationTimeout { get; }
}

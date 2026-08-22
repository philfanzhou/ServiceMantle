using ServiceMantle;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// Contains the instance-local bootstrap information needed by later database modules.
/// </summary>
public sealed class BootstrapConfiguration
{
    /// <summary>
    /// Initializes a bootstrap configuration.
    /// </summary>
    /// <param name="serviceId">The service to which the bootstrap file belongs.</param>
    /// <param name="database">The database configuration.</param>
    /// <param name="masterKey">The master key used by later data-protection modules.</param>
    /// <param name="sourcePath">The optional source file path.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The master key is empty or whitespace.</exception>
    public BootstrapConfiguration(
        ServiceId serviceId,
        BootstrapDatabaseConfiguration database,
        string masterKey,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(masterKey);

        if (string.IsNullOrWhiteSpace(masterKey))
        {
            throw new ArgumentException(
                "The bootstrap master key cannot be empty.",
                nameof(masterKey));
        }

        ServiceId = serviceId;
        Database = database;
        MasterKey = masterKey.Trim();
        SourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath);
    }

    /// <summary>
    /// Gets the service identifier associated with the bootstrap file.
    /// </summary>
    public ServiceId ServiceId { get; }

    /// <summary>
    /// Gets the database bootstrap configuration.
    /// </summary>
    public BootstrapDatabaseConfiguration Database { get; }

    /// <summary>
    /// Gets the master key required by later encryption modules.
    /// </summary>
    public string MasterKey { get; }

    /// <summary>
    /// Gets the absolute source path when the configuration was loaded from a file.
    /// </summary>
    public string? SourcePath { get; }

    /// <summary>
    /// Returns a representation containing only safe diagnostic information.
    /// </summary>
    public override string ToString()
    {
        var source = SourcePath ?? "<memory>";
        return $"BootstrapConfiguration(ServiceId={ServiceId.Value}, Provider={Database.Provider}, SourcePath={source})";
    }
}

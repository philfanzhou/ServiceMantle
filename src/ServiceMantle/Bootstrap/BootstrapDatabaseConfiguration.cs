namespace ServiceMantle.Bootstrap;

/// <summary>
/// Describes the database information required to open a service database.
/// </summary>
public sealed class BootstrapDatabaseConfiguration
{
    /// <summary>
    /// Gets the canonical database provider name.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// Gets the optional database server version.
    /// </summary>
    public string? ServerVersion { get; }

    /// <summary>
    /// Gets the database connection string.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Initializes a database bootstrap configuration.
    /// </summary>
    /// <param name="provider">The canonical provider name, either PostgreSQL or SQLite.</param>
    /// <param name="serverVersion">The optional database server version.</param>
    /// <param name="connectionString">The database connection string.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    public BootstrapDatabaseConfiguration(
        string provider,
        string? serverVersion,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(connectionString);

        var normalizedProvider = provider.Trim();

        Provider = normalizedProvider switch
        {
            "PostgreSQL" or "SQLite" => normalizedProvider,
            _ => throw new ArgumentException(
                "The database provider must be PostgreSQL or SQLite.",
                nameof(provider))
        };

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "The database connection string cannot be empty.",
                nameof(connectionString));
        }

        ServerVersion = string.IsNullOrWhiteSpace(serverVersion)
            ? null
            : serverVersion.Trim();
        ConnectionString = connectionString.Trim();
    }

    /// <summary>
    /// Returns a representation that excludes the connection string.
    /// </summary>
    public override string ToString() =>
        $"BootstrapDatabaseConfiguration(Provider={Provider})";
}

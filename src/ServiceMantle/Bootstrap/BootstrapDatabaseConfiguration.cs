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
    /// <param name="provider">The canonical database provider identifier.</param>
    /// <param name="serverVersion">The optional database server version.</param>
    /// <param name="connectionString">The database connection string.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    public BootstrapDatabaseConfiguration(
        string provider,
        string? serverVersion,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(provider);

        Provider = NormalizeProvider(provider);

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

    private static string NormalizeProvider(string provider)
    {
        var normalized = provider.Trim();

        if (normalized.Length is 0)
        {
            throw new ArgumentException(
                "The database provider cannot be empty.",
                nameof(provider));
        }

        if (normalized.Length > 64)
        {
            throw new ArgumentException(
                "The database provider is too long.",
                nameof(provider));
        }

        if (!IsAsciiLetterOrDigit(normalized[0]))
        {
            throw new ArgumentException(
                "The database provider identifier must start with an ASCII letter or digit.",
                nameof(provider));
        }

        for (var i = 1; i < normalized.Length; i++)
        {
            var current = normalized[i];
            if (!IsAsciiLetterOrDigit(current) && current is not ('.' or '-' or '_'))
            {
                throw new ArgumentException(
                    "The database provider identifier contains invalid characters.",
                    nameof(provider));
            }
        }

        return normalized;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

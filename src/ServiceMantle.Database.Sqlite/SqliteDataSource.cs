using Microsoft.Data.Sqlite;

namespace ServiceMantle.Database.Sqlite;

/// <summary>
/// Resolves a SQLite connection string's <c>Data Source</c> against an explicit base directory,
/// independent of the process working directory.
/// </summary>
/// <remarks>
/// <para>
/// The resolution covers exactly the data source path: every other connection-string parameter
/// is preserved semantically, in-memory databases and <c>file:</c> URIs are returned unchanged,
/// an already-absolute data source is returned unchanged, and a relative data source is combined
/// with the supplied base directory and normalized. The method never touches the file system,
/// never creates a directory, and never resolves symbolic links.
/// </para>
/// <para>
/// A relative data source resolved here and then handed to the ServiceMantle SQLite target
/// identity normalization resolves to the same file: both use the same path normalization.
/// </para>
/// Not guaranteed: two different paths may still denote the same file through symbolic links or
/// mount aliases; the caller owns choosing the correct base directory (usually the content root).
/// </remarks>
public static class SqliteDataSource
{
    /// <summary>
    /// Resolves the <c>Data Source</c> of a SQLite connection string against an explicit absolute
    /// base directory.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string to resolve.</param>
    /// <param name="baseDirectory">
    /// The absolute base directory a relative data source resolves against, usually the host's
    /// content root.
    /// </param>
    /// <returns>
    /// A semantically equal connection string whose <c>Data Source</c> is absolute; in-memory
    /// databases and <c>file:</c> URIs are returned unchanged.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The connection string is null, empty, or unparsable; its data source is empty; or the base
    /// directory is null, empty, or not an absolute path. Exceptions never echo the connection
    /// string.
    /// </exception>
    public static string ResolveConnectionString(string connectionString, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException(
                "The base directory must be an absolute path.",
                nameof(baseDirectory));
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException(
                "The connection string is not a valid SQLite connection string.",
                nameof(connectionString),
                exception);
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException(
                "The connection string has an empty data source.",
                nameof(connectionString));
        }

        if (dataSource.IndexOf('\0') >= 0 ||
            dataSource.Contains("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The data source uses an unsupported substitution.",
                nameof(connectionString));
        }

        // In-memory databases and file URIs stay exactly as the caller wrote them.
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        if (Path.IsPathFullyQualified(dataSource))
        {
            // Already anchored: nothing is rewritten, including the caller's separators.
            return connectionString;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(baseDirectory, dataSource));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "The relative data source cannot be resolved against the base directory.",
                nameof(connectionString),
                exception);
        }

        // Round-tripping through the builder keeps every other parameter; only the data source
        // is replaced.
        builder.DataSource = resolved;
        return builder.ConnectionString;
    }
}

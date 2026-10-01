using Npgsql;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>
/// Derives the PostgreSQL maintenance connection string from a target connection string: the
/// same user, host, port, and credentials, with only the database changed to
/// <see cref="MaintenanceDatabaseName"/>.
/// </summary>
/// <remarks>
/// The derived value introduces no additional or persisted administrative secret; it stays in
/// memory and is used only for the duration of a preparation call. Multi-host targets,
/// multiplexing, and other unusable shapes are not rejected here: the preparation provider
/// itself validates the maintenance connection it receives. The derivation never reads or
/// writes the file system and never opens a connection.
/// </remarks>
public static class PostgreSqlMaintenanceConnection
{
    /// <summary>The fixed maintenance database name used by the derived connection.</summary>
    public const string MaintenanceDatabaseName = "postgres";

    /// <summary>
    /// Derives the maintenance connection string from a target connection string by replacing
    /// only the database name.
    /// </summary>
    /// <param name="targetConnectionString">The target database connection string.</param>
    /// <returns>
    /// A connection string equal to the target's except for the database name, which becomes
    /// <see cref="MaintenanceDatabaseName"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The target connection string is null, empty, or not a valid PostgreSQL connection string.
    /// The exception never echoes the connection string.
    /// </exception>
    public static string DeriveConnectionString(string targetConnectionString)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionString))
        {
            throw new ArgumentException(
                "The target connection string cannot be empty.",
                nameof(targetConnectionString));
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(targetConnectionString)
            {
                Database = MaintenanceDatabaseName,
            };

            return builder.ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException(
                "The target connection string is not a valid PostgreSQL connection string.",
                nameof(targetConnectionString),
                exception);
        }
    }
}

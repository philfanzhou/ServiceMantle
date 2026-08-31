using Microsoft.Data.SqlClient;

namespace ServiceMantle.Database.SqlServer;

internal static class SqlServerDatabaseTarget
{
    // CREATE DATABASE generates a logical log-file name when no file list is supplied. SQL Server
    // appends a suffix to the database name, reducing the safe database-name limit from 128 to 123.
    internal const int MaximumDatabaseNameLength = 123;
    internal const int MinimumSupportedServerMajorVersion = 15;
    internal const int MaximumConnectTimeoutSeconds = 8;
    internal const int CommandTimeoutSeconds = 5;

    internal static bool TryBuildConnectionString(
        string connectionString,
        out SqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            builder = null!;
            return false;
        }
    }

    internal static bool TryGetValidDatabaseName(
        SqlConnectionStringBuilder builder,
        out string databaseName)
    {
        databaseName = builder.InitialCatalog;
        // Opening a connection with AttachDBFilename can attach a database as a side effect. This
        // provider supports only an already server-hosted database target, so both validation and
        // observation must reject auto-attach before opening a connection.
        return string.IsNullOrEmpty(builder.AttachDBFilename) && IsValidDatabaseName(databaseName);
    }

    internal static bool IsValidDatabaseName(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) ||
            databaseName.Length > MaximumDatabaseNameLength ||
            databaseName[^1] == ' ')
        {
            return false;
        }

        foreach (var character in databaseName)
        {
            if (character == '\0' || char.IsControl(character) || char.IsSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static void ApplySafeTimeouts(SqlConnectionStringBuilder builder)
    {
        builder.ConnectTimeout = builder.ConnectTimeout <= 0
            ? MaximumConnectTimeoutSeconds
            : Math.Min(builder.ConnectTimeout, MaximumConnectTimeoutSeconds);
        builder.CommandTimeout = builder.CommandTimeout <= 0
            ? CommandTimeoutSeconds
            : Math.Min(builder.CommandTimeout, CommandTimeoutSeconds);
        builder.ConnectRetryCount = 0;
    }

    internal static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}

using MySqlConnector;

namespace ServiceMantle.Database.MySql;

internal static class MySqlDatabaseTarget
{
    internal const int MaximumDatabaseNameLength = 64;
    internal const uint MaximumConnectTimeoutSeconds = 8;
    internal const uint CommandTimeoutSeconds = 5;

    internal static bool TryBuildConnectionString(
        string connectionString,
        out MySqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException)
        {
            builder = null!;
            return false;
        }
    }

    internal static bool TryGetValidDatabaseName(
        MySqlConnectionStringBuilder builder,
        out string databaseName)
    {
        databaseName = builder.Database;
        return IsValidDatabaseName(databaseName);
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

    internal static void ApplySafeTimeouts(MySqlConnectionStringBuilder builder)
    {
        builder.ConnectionTimeout = builder.ConnectionTimeout == 0
            ? MaximumConnectTimeoutSeconds
            : Math.Min(builder.ConnectionTimeout, MaximumConnectTimeoutSeconds);
        builder.DefaultCommandTimeout = builder.DefaultCommandTimeout == 0
            ? CommandTimeoutSeconds
            : Math.Min(builder.DefaultCommandTimeout, CommandTimeoutSeconds);
    }

    internal static string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    internal static bool MatchesDatabaseIdentifierRules(
        bool exactMatch,
        bool caseFoldedMatch,
        int lowerCaseTableNames) =>
        exactMatch || (lowerCaseTableNames is 1 or 2 && caseFoldedMatch);
}

using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.PostgreSql;

internal static class PostgreSqlProviderId
{
    internal const string PostgresAlias = "postgres";

    internal static bool IsSupported(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.PostgreSql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, PostgresAlias, StringComparison.OrdinalIgnoreCase);
}

using Xunit;

namespace ServiceMantle.Testing;

public enum RealDatabaseAvailability
{
    Available,
    OptionalUnavailable,
    RequiredUnavailable
}

/// <summary>
/// Applies the shared opt-in and required-environment policy for real-database tests.
/// </summary>
public static class RealDatabaseTestEnvironment
{
    public static string GetRequirementVariable(RealDatabaseProvider provider) => provider switch
    {
        RealDatabaseProvider.PostgreSql => "RUN_SERVICEMANTLE_POSTGRES_TESTS",
        RealDatabaseProvider.MySql => "RUN_SERVICEMANTLE_MYSQL_TESTS",
        RealDatabaseProvider.MariaDb => "RUN_SERVICEMANTLE_MARIADB_TESTS",
        RealDatabaseProvider.Oracle => "RUN_SERVICEMANTLE_ORACLE_TESTS",
        RealDatabaseProvider.SqlServer => "RUN_SERVICEMANTLE_SQLSERVER_TESTS",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static bool IsRequired(RealDatabaseProvider provider) =>
        string.Equals(
            Environment.GetEnvironmentVariable(GetRequirementVariable(provider)),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static RealDatabaseAvailability Evaluate(bool isRequired, bool isAvailable)
    {
        if (isAvailable)
        {
            return RealDatabaseAvailability.Available;
        }

        return isRequired
            ? RealDatabaseAvailability.RequiredUnavailable
            : RealDatabaseAvailability.OptionalUnavailable;
    }

    public static void RequireAvailable(RealDatabaseProvider provider, bool isAvailable)
    {
        switch (Evaluate(IsRequired(provider), isAvailable))
        {
            case RealDatabaseAvailability.Available:
                return;
            case RealDatabaseAvailability.OptionalUnavailable:
                Assert.Skip("The optional real-database test environment is unavailable.");
                return;
            case RealDatabaseAvailability.RequiredUnavailable:
                throw new RealDatabaseTestEnvironmentException(provider);
            default:
                throw new InvalidOperationException("The real-database availability decision is invalid.");
        }
    }
}

public sealed class RealDatabaseTestEnvironmentException : Exception
{
    public RealDatabaseTestEnvironmentException(RealDatabaseProvider provider)
        : base($"The required {provider} real-database test environment is unavailable.")
    {
        Provider = provider;
    }

    public RealDatabaseProvider Provider { get; }
}

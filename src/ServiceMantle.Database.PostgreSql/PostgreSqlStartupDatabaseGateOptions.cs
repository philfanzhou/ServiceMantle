using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>Pure, explicit PostgreSQL startup database gate option presets.</summary>
public static class PostgreSqlStartupDatabaseGateOptions
{
    /// <summary>Creates the multi-instance preset with preparation enabled, thirty-second budgets,
    /// and a maintenance connection derived from the target. Creation permission is explicit.</summary>
    /// <remarks>This factory neither registers services nor validates reachability, privileges or lock availability.</remarks>
    /// <exception cref="ArgumentException">The target provider or connection syntax is invalid. Parser diagnostics are not exposed.</exception>
    public static StartupDatabaseGateOptions Create(BootstrapDatabaseConfiguration database, bool allowTargetCreation)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!string.Equals(database.Provider, WellKnownDatabaseProviderIds.PostgreSql, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The database target is invalid (database_target_preparation.invalid_target).", nameof(database));
        string maintenance;
        try { maintenance = PostgreSqlMaintenanceConnection.DeriveConnectionString(database.ConnectionString); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new ArgumentException("The database connection string is invalid (database.connection_string_invalid).", nameof(database));
        }
        return new(database, DatabaseDeploymentMode.MultiInstance, TimeSpan.FromSeconds(30),
            enableTargetPreparation: true, allowTargetCreation: allowTargetCreation,
            maintenanceConnectionString: maintenance, preparationTimeout: TimeSpan.FromSeconds(30));
    }
}

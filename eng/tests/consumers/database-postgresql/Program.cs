// The PostgreSQL provider package's first dedicated consumer (#610): it names the package's
// public surface — the bootstrap provider, target preparation, the maintenance-connection
// derivation, the health probe failure classifier (#606), the migration lock provider, and the
// schema evidence reader — through the namespaces those types require, next to Npgsql which the
// package itself brings along.
//
// Nothing here opens a connection, starts a container, or touches a database: every provider
// entry is either constructed (the two parameterless providers own no resources until called),
// invoked as a pure derivation, or named by type only. The package's Testcontainers tests own
// the behavioral surface; this file owns the compile surface against the packed artifact.
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Database.PostgreSql.Migration;

// The maintenance-connection derivation is a pure function of the target connection string: it
// never opens a connection, and its result is only compared here, never printed.
var derived = PostgreSqlMaintenanceConnection.DeriveConnectionString(
    "Host=consumer-example;Port=5432;Username=consumer;Password=placeholder;Database=consumer_db");
if (!derived.Contains(PostgreSqlMaintenanceConnection.MaintenanceDatabaseName, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "The maintenance connection derivation did not switch to the maintenance database.");
}

// The provider entries, constructed without any I/O: the bootstrap validator and the target
// preparation provider hold no connection until a preparation call supplies one.
_ = new PostgreSqlBootstrapDatabaseProvider();
_ = new PostgreSqlDatabaseTargetPreparationProvider();

// The health probe classifier (#606) and the migration lock provider, named next to the core
// seams they implement; the classifier answers one pure classification for a fabricated
// connection-class exception without any driver involvement.
var classifier = new PostgreSqlDatabaseProbeFailureClassifier();
if (classifier.Classify(new NpgsqlException("consumer classification probe")) !=
    ServiceDatabaseProbeFailureKind.ConnectionFailure)
{
    throw new InvalidOperationException(
        "The PostgreSQL probe failure classifier did not classify a connection failure.");
}

// The remaining public surface, named unqualified with every namespace above in scope: a name
// shared with an Npgsql or core type would fail this compilation rather than a consumer's.
// The schema evidence reader (#610) is only named here — reading runs on a caller connection
// against a real database, which belongs to the package's gated tests, never to this consumer.
ReportType(typeof(PostgreSqlSchemaEvidenceReader));
ReportType(typeof(PostgreSqlMigrationLockProvider));
ReportType(typeof(PostgreSqlDatabaseProbeFailureClassifier));
ReportType(typeof(PostgreSqlDatabaseTargetPreparationProvider));
ReportType(typeof(PostgreSqlBootstrapDatabaseProvider));
ReportType(typeof(PostgreSqlMaintenanceConnection));

Console.WriteLine(
    "PostgreSQL provider consumer verified the capability namespaces: bootstrap validation, " +
    "target preparation, the health probe classifier, the migration lock provider, and the " +
    $"schema evidence reader {typeof(PostgreSqlSchemaEvidenceReader).FullName}.");

var preset = PostgreSqlStartupDatabaseGateOptions.Create(new BootstrapDatabaseConfiguration(
    WellKnownDatabaseProviderIds.PostgreSql, "16", "Host=example;Database=app"), allowTargetCreation: false);
if (!preset.EnableTargetPreparation || preset.AllowTargetCreation || preset.DeploymentMode != DatabaseDeploymentMode.MultiInstance)
    throw new InvalidOperationException("The explicit PostgreSQL startup preset did not resolve.");

static void ReportType(Type type) => Console.WriteLine($"Resolved {type.FullName}.");

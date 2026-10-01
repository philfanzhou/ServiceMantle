// The EF Core persistence package's own consumer (#577, closing the ADR 0007 gap): it names the
// package's public surface through the capability namespaces ServiceMantle.Persistence.Relational,
// .Mapping, .DataProtection, and .Stores, next to the framework namespaces those types require.
//
// Microsoft.AspNetCore.DataProtection and Microsoft.EntityFrameworkCore are imported at the same
// time on purpose. Reaching the Data Protection entry and the mapping extensions requires both,
// and Data Protection alone ships its own DataProtectionBuilderExtensions, so an ordinary
// ServiceMantle type that started sharing a name with a framework type in scope would surface
// here as a CS0104 build failure instead of in a consumer's build.
//
// Nothing here starts a host or touches a database: the key-ring registration is inspected as
// descriptors, the consumer context is only compiled (its OnModelCreating resolves every mapping
// extension), and no store is constructed against a live connection. The package's database tests
// own the behavioral surface; this file owns the compile surface.
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceMantle;
using ServiceMantle.Health;
using ServiceMantle.Persistence.Relational;
using ServiceMantle.Persistence.Relational.DataProtection;
using ServiceMantle.Persistence.Relational.Mapping;
using ServiceMantle.Persistence.Relational.Stores;

var services = new ServiceCollection();

// The Data Protection entry keeps its product-identifying method name and its EfCore* class name;
// it is reached through the capability sub-namespace next to the framework Data Protection using.
services.AddDataProtection()
    .PersistKeysToServiceMantleEfCore<ConsumerDbContext>(
        ServiceId.Parse("persistence-consumer"),
        _ => "persistence-consumer-root-key-placeholder");

// The entry registers the repository as its own singleton and wires it into the framework's
// KeyManagementOptions through an options configuration; both shapes are asserted as descriptors,
// so no provider is built and no database work can happen here.
if (!services.Any(descriptor =>
        descriptor.ServiceType == typeof(EfCoreDataProtectionKeyRepository<ConsumerDbContext>)) ||
    !services.Any(descriptor =>
        descriptor.ServiceType == typeof(IConfigureOptions<KeyManagementOptions>)))
{
    throw new InvalidOperationException(
        "The EF Core key-ring entry did not register its repository and options wiring.");
}

// The health snapshot source entry (#606): like every AddServiceMantle* registration entry it
// lives in Microsoft.Extensions.DependencyInjection (ADR 0007 C), so this composition root
// reaches it through the using it already has; the classifier comes from the consumer (a
// provider package such as ServiceMantle.Database.PostgreSql supplies the real one), and the
// probe mode is this package's own capability type. Inspected as descriptors only; the startup
// receipt and the scoped context resolve at request time, never here.
services.AddServiceMantleEfCoreHealthSnapshotSource<ConsumerDbContext>(
    ServiceId.Parse("persistence-consumer"),
    new ConsumerProbeFailureClassifier(),
    EfCoreHealthSnapshotProbeMode.MappedSchema,
    errorCodePrefix: "persistence-consumer.health");

if (!services.Any(descriptor =>
        descriptor.ServiceType == typeof(IServiceHealthSnapshotSource) &&
        descriptor.Lifetime == ServiceLifetime.Scoped))
{
    throw new InvalidOperationException(
        "The EF Core health snapshot entry did not register the scoped snapshot source.");
}

Console.WriteLine(
    "Persistence EF Core consumer verified the capability namespaces: the key ring registers " +
    $"through {typeof(EfCoreDataProtectionKeyRepository<>).FullName}, the consumer context " +
    $"implements {typeof(IServiceDbContext).FullName}, and the audit dialect is " +
    $"{typeof(ManagementAuditDatabaseDialect).FullName}.{ManagementAuditDatabaseDialect.Sqlite}.");

// The remaining public surface, named unqualified with every framework namespace above in scope:
// a name shared with a framework type fails this compilation rather than a consumer's.
ReportType(typeof(EfCoreDataProtectionExtensions));
ReportType(typeof(ServiceMantleEfCoreHealthSnapshotServiceCollectionExtensions));
ReportType(typeof(EfCoreHealthSnapshotSource<>));
ReportType(typeof(EfCoreHealthSnapshotProbeMode));
ReportType(typeof(IServiceDatabaseProbeFailureClassifier));
ReportType(typeof(ServiceDatabaseProbeFailureKind));
ReportType(typeof(ModelBuilderExtensions));
ReportType(typeof(DataProtectionKeyModelBuilderExtensions));
ReportType(typeof(ManagementAuditModelBuilderExtensions));
ReportType(typeof(ServiceSettingModelBuilderExtensions));
ReportType(typeof(WellKnownDataProtectionKeyRepositoryErrorCodes));
ReportType(typeof(DataProtectionKeyRepositoryException));
ReportType(typeof(ServiceInstallationEntity));
ReportType(typeof(EfCoreServiceInstallationStore<>));
ReportType(typeof(EfCoreServiceSettingStore<>));
ReportType(typeof(EfCoreServiceSettingUpdateTransaction<>));
ReportType(typeof(EfCoreServiceSetupCodeStore<>));
ReportType(typeof(EfCoreManagementAuditWriter<>));
ReportType(typeof(EfCoreManagementAuditQueryService<>));

static void ReportType(Type type) => Console.WriteLine($"Resolved {type.FullName}.");

/// <summary>
/// The consumer's stand-in for a provider classifier: an ordinary consumer-supplied
/// implementation of the core Health seam, unclassified by default.
/// </summary>
internal sealed class ConsumerProbeFailureClassifier : IServiceDatabaseProbeFailureClassifier
{
    public ServiceDatabaseProbeFailureKind Classify(Exception exception) =>
        ServiceDatabaseProbeFailureKind.Unclassified;
}

/// <summary>
/// The consumer's own business context: it owns saving, transactions, and migrations, and maps the
/// ServiceMantle tables through the persistence package's public mapping extensions. It is never
/// instantiated by this consumer; compiling its members is the resolution assertion.
/// </summary>
internal sealed class ConsumerDbContext(DbContextOptions<ConsumerDbContext> options)
    : DbContext(options), IServiceDbContext
{
    public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleInstallation();
        modelBuilder.AddServiceMantleDataProtectionKeys();
        modelBuilder.AddServiceMantleSettings();
        modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
    }
}

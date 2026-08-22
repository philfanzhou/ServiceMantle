# ServiceMantle

ServiceMantle is a shared .NET 10 library for reusable service-management foundations used by ASP.NET Core services.

Current status: **early development**. Service identity, installation phase primitives, and the instance-local Bootstrap file model are implemented; database integration, management authentication, auditing, observability, and service discovery capabilities are not yet implemented.

`ServiceId` is a stable deployment-level identifier shared by all instances of one service. `InstanceId` identifies one running instance for runtime diagnostics and must not be used as a substitute for `ServiceId`.

## Phase 1 scope

- bootstrap
- installation
- configuration
- administration abstraction
- auditing
- observability
- discovery

## Instance-local Bootstrap

The Bootstrap file is an instance-local Secret. It contains the information needed to open and decrypt the business database: the format version, service identifier, database provider, optional server version, connection string, and MasterKey. ServiceMantle does not encrypt this file because the key for doing so is outside this library's responsibility.

By default, the file is stored at:

```text
<AppContext.BaseDirectory>/config/<normalized-service-id>.bootstrap.json
```

The store distinguishes a missing file from a damaged or invalid file. `TryLoad()` returns `null` only when the file is absent; invalid JSON, unknown fields, missing required values, unsupported versions, and service-id mismatches raise `BootstrapException`. `Create()` is for first creation and never overwrites an existing file. `Replace()` atomically replaces an existing file.

```csharp
var serviceId = ServiceId.Parse("signacore");
var store = new BootstrapFileStore(serviceId);

var bootstrap = new BootstrapConfiguration(
    serviceId,
    new BootstrapDatabaseConfiguration(
        "PostgreSQL",
        "15",
        connectionString),
    masterKey);

store.Create(bootstrap);
var loaded = store.Load();
```

Bootstrap files belong to individual service instances. Synchronizing or distributing them across multiple instances is not a current ServiceMantle responsibility. When backing up a business database, also back up the matching Bootstrap file for the instance that owns it.

## Bootstrap management use cases

`BootstrapConfigurationManager` is the use-case layer intended for a future management API. Its status projection reports service and instance identity, provider metadata, and whether secret values are configured, but never returns the connection string or MasterKey. Create and update requests are assembled into a complete candidate configuration and must pass an `IBootstrapCandidateValidator` before the local Bootstrap file is written. Updates preserve omitted replacement values and use the existing atomic file replacement semantics.

Bootstrap changes affect only the current instance's local Bootstrap file and return `RestartRequired=true`; the process must be restarted before a change is activated. HTTP endpoints, real database connectivity validation, administrator authentication, and multi-instance synchronization are not implemented yet.

## Installation persistence foundation

`ServiceMantle` core stays provider-agnostic and does not reference EF Core.

`ServiceMantle.Persistence.EntityFrameworkCore` is an optional package that defines:

- `ServiceInstallationEntity` mapping for `service_installations`.
- `IServiceMantleDbContext` contract that business DbContexts implement.
- `ModelBuilder` extension `AddServiceMantleInstallation()` for model registration.
- `EfCoreServiceInstallationStore<TDbContext>` implementing `IServiceInstallationStore`.

This layer only standardizes installation state access; it does not generate migrations, execute `Database.MigrateAsync`, or assume ownership of bootstrap secrets. `service_installations` rows are owned by the consuming service database.

Management consumers should implement a minimal integration model, for example:

```csharp
public sealed class MyDbContext : DbContext, IServiceMantleDbContext
{
    public DbSet<ServiceInstallationEntity> ServiceInstallations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleInstallation();
    }
}
```

For now, only `service_installations` is defined. Planned future tables (not in this release):

- `service_settings`
- `service_audit_logs`
- `service_data_protection_keys`
- Setup code metadata
- administrator identity/state tables

Shared migration ownership is intentionally moved to the consuming service. In deployments against existing business databases, services must create migration entries and keep installation table ownership in their own startup/deployment process.

## Provider SPI and validation dispatch

The core library now provides a provider SPI so validation can be extended without changing the core package.

- `IBootstrapDatabaseProvider` implementations are expected in optional provider packages and receive only `BootstrapDatabaseConfiguration`.
- `BootstrapDatabaseProviderRegistry` resolves providers by canonical `Provider` id and aliases using case-insensitive matching.
- `BootstrapDatabaseCandidateValidator` performs generic checks (`database.provider_not_registered`, server-version constraints) and then dispatches to the matched provider.
- Provider IDs in bootstrap files are validated for safe syntax only; registration and driver-specific behavior are handled by the candidate validator at management time.
- Driver packages are distributed separately, so the ServiceMantle core package stays free of database driver dependencies.

Current and planned provider packages are:

- `ServiceMantle.Database.PostgreSql` validates PostgreSQL settings and performs a minimum read probe (`SELECT 1`) against the target database.
- `ServiceMantle.Persistence.EntityFrameworkCore` provides shared install-state persistence and consumption patterns.
- `ServiceMantle.Database.SQLite`
- `ServiceMantle.Database.MySql`
- `ServiceMantle.Database.MariaDb`
- `ServiceMantle.Database.Oracle`
- `ServiceMantle.Database.SqlServer`

The PostgreSQL provider only validates configuration and target connectivity. It does not create databases, migrate schemas, manage history tables, or provide multi-instance locking.

MySQL and MariaDB keep independent provider IDs even if they can share lower-level behavior.
Oracle is planned as a `ServerSchema`-style target provider; SQL Server and SQLite follow their own target semantics.

## Non-goals (first version)

- No product-specific user / OAuth / JWT domain models.
- No management frontend.
- No business migration or service-specific logic.

## Frontend note

Frontend work is intentionally out of scope and will be implemented in a separate `ServiceMantle.Console` project.

## Repository layout

- `src/ServiceMantle/ServiceMantle.csproj`
- `tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj`
- `src/ServiceMantle/ServiceId.cs`
- `src/ServiceMantle/InstanceId.cs`
- `src/ServiceMantle/Installation/`
- `src/ServiceMantle.Persistence.EntityFrameworkCore/ServiceMantle.Persistence.EntityFrameworkCore.csproj`
- `tests/ServiceMantle.Tests/ServiceIdTests.cs`
- `tests/ServiceMantle.Tests/InstanceIdTests.cs`
- `tests/ServiceMantle.Tests/Installation/`
- `tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests.csproj`
- `ServiceMantle.slnx`
- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props`

## Local build commands

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack src/ServiceMantle/ServiceMantle.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Persistence.EntityFrameworkCore/ServiceMantle.Persistence.EntityFrameworkCore.csproj -c Release --no-build
```

# ServiceMantle

ServiceMantle is a shared .NET 10 library for reusable service-management foundations used by ASP.NET Core services.

Current status: **early development**. Service identity, installation phase primitives, the instance-local Bootstrap file model, database migration orchestration, the PostgreSQL advisory lock provider, and the optional database target preparation capability (PostgreSQL server-database preparation) are implementation-complete and verified against real PostgreSQL via Testcontainers. Management authentication, auditing, observability, and service discovery capabilities are not yet implemented.

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

- `ServiceMantle.Database.PostgreSql` validates PostgreSQL settings, performs a minimum read probe (`SELECT 1`) against the target database, and provides session-level advisory lock capability for multi-instance migration coordination (implementation complete, pending CI container verification).
- `ServiceMantle.Persistence.EntityFrameworkCore` provides shared install-state persistence and consumption patterns.
- `ServiceMantle.Database.SQLite`
- `ServiceMantle.Database.MySql`
- `ServiceMantle.Database.MariaDb`
- `ServiceMantle.Database.Oracle`
- `ServiceMantle.Database.SqlServer`

The PostgreSQL provider validates configuration and target connectivity. It also provides session-level advisory lock capability for safe multi-instance migration coordination.

MySQL and MariaDB keep independent provider IDs even if they can share lower-level behavior.
Oracle is planned as a `ServerSchema`-style target provider; SQL Server and SQLite follow their own target semantics.

## Database target preparation

Database target preparation is a separate, optional capability from bootstrap validation. A provider that implements `IBootstrapDatabaseProvider` does not automatically support preparing (creating) a missing target; a provider opts in only by also registering an `IDatabaseTargetPreparationProvider` implementation. Callers resolve this capability through `DatabaseTargetPreparationProviderRegistry` and must fail closed with `database_target_preparation.capability_not_supported` when no preparation provider is registered for a database provider id, rather than treating an unsupported provider as already prepared.

The capability models three target kinds via the existing `BootstrapDatabaseTargetKind` enum (`ServerDatabase`, `File`, `ServerSchema`), and exposes three independent observation signals:

- **Server reachable** (`DatabaseTargetObservation.IsServerReachable`) — the database server responded to the connection attempt.
- **Target exists** (`DatabaseTargetObservation.TargetExists`) — `true` when existence was proved, `false` when absence was proved, and `null` when the connection failed before existence could be established.
- **Target connectable** (`DatabaseTargetObservation.IsTargetConnectable`) — a connection to the target itself succeeded.

```csharp
var preparationProviders = new DatabaseTargetPreparationProviderRegistry(
    [new PostgreSqlDatabaseTargetPreparationProvider()]);

if (!preparationProviders.TryGetProvider(bootstrapDatabaseConfiguration.Provider, out var provider))
{
    // Fail closed: this provider does not support target preparation.
    return;
}

var observation = await provider!.ObserveAsync(bootstrapDatabaseConfiguration, cancellationToken);
if (observation.IsTargetConnectable)
{
    return; // Already ready; nothing to prepare.
}

if (observation.TargetExists is not false)
{
    // Authentication failed, existence is unknown, or an existing target is not connectable.
    // Preserve the observation failure instead of turning it into a false AlreadyExists success.
    logger.LogError("Target is not connectable: {ErrorCode}", observation.ErrorCode);
    return;
}

// AdministrativeConnectionString is used only for the duration of this call. It is never
// persisted, logged, included in diagnostics, or returned in any result.
var request = new DatabaseTargetPreparationRequest(bootstrapDatabaseConfiguration, administrativeConnectionString);
var result = await provider.PrepareAsync(request, timeout: TimeSpan.FromSeconds(30), cancellationToken);

if (!result.Succeeded)
{
    logger.LogError("Target preparation failed: {ErrorCode}", result.ErrorCode);
    return;
}

var preparedObservation = await provider.ObserveAsync(bootstrapDatabaseConfiguration, cancellationToken);
if (!preparedObservation.IsTargetConnectable)
{
    logger.LogError("Prepared target is not connectable: {ErrorCode}", preparedObservation.ErrorCode);
}
```

`DatabaseTargetPreparationResult.Outcome` reports `Created` or `AlreadyExists`. Implementations must never overwrite, drop, recreate, or otherwise destructively modify a target that already exists.

### PostgreSQL target preparation

`ServiceMantle.Database.PostgreSql.PostgreSqlDatabaseTargetPreparationProvider` observes a PostgreSQL target with a single connection attempt. A structured "database does not exist" response (SQLSTATE `3D000`) proves the server is reachable and the target is missing. Authentication errors can occur before PostgreSQL checks the database name, so those observations report a reachable server with `TargetExists == null`; target-level `CONNECT` denial (`42501`) reports a known existing but unreachable target. `PrepareAsync` uses the caller-supplied administrative connection string with pooling forcibly disabled to check `pg_database` and, only when the target is absent, issue `CREATE DATABASE ... OWNER ...`; the owner is the target connection string's PostgreSQL username and must already exist as a role. Database and owner names must be valid Unicode without control characters and no more than 63 bytes in the connected server's actual encoding, preventing PostgreSQL's silent identifier truncation without rejecting legal names on non-UTF-8 servers. A genuinely concurrent creation race — observed as either the `duplicate_database` error (`42P04`) or a unique-key violation on the `pg_database` name index (`23505`), depending on timing — is verified against `pg_database` and treated as `AlreadyExists`, not a failure.

### Error codes

Safe error codes for database target preparation failures are restricted to this allowlist; result and observation factories reject arbitrary text:
- `database_target_preparation.capability_not_supported` - No preparation provider registered for the database provider.
- `database_target_preparation.provider_mismatch` - The target does not identify this provider's database provider.
- `database_target_preparation.invalid_target` - The target or administrative connection information is not usable.
- `database_target_preparation.server_unreachable` - The database server could not be reached.
- `database_target_preparation.authentication_failed` - The server rejected the supplied credentials before target existence could be established.
- `database_target_preparation.permission_denied` - The target connection or administrative operation lacked permission.
- `database_target_preparation.target_conflict` - Creation collided with an existing, differently-owned object of the same name.
- `database_target_preparation.connection_failed` - A connection could not be established or was lost while preparing the target.
- `database_target_preparation.timeout` - The preparation operation exceeded its allotted timeout.
- `database_target_preparation.preparation_failed` - Preparation failed for a provider-specific reason not covered by another code.

Caller-requested cancellation is always propagated as a sanitized `OperationCanceledException` without the underlying database exception, distinct from a timeout failure result. PostgreSQL preparation rejects infinite, non-positive, and timer-unsupported timeouts before starting work; an already-cancelled caller token takes precedence over timeout validation.

## Database migration orchestration

ServiceMantle provides provider-agnostic migration orchestration that ensures safe multi-instance execution of consuming service migrations under an optional provider-specific lock.

The orchestration flow:
1. Acquire a provider-specific migration lock (e.g., PostgreSQL advisory lock).
2. Inspect the current database state.
3. Skip migration if the database is already at the current compatible version (allowing waiting instances to pass without re-executing).
4. Fail closed if the database version is newer than the application supports.
5. Execute the consuming service's complete migration workflow exactly once.
6. Re-inspect the database state to ensure migration succeeded.
7. Always release the lock, even on failure or cancellation.

The consuming service implements `IDatabaseMigrationExecutor` to define its migration logic:

```csharp
public interface IDatabaseMigrationExecutor
{
    // Inspect database state: Empty, CurrentVersionCompatible, PendingMigration, VersionTooNew, or InspectionFailed
    ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default);

    // Execute the complete migration workflow
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
}
```

The `DatabaseMigrationOrchestrator` is instantiated with the executor and a lock provider registry:

```csharp
var executor = new MyServiceMigrationExecutor(dbContext);
var lockProviders = new DatabaseMigrationLockProviderRegistry([new PostgreSqlMigrationLockProvider()]);
var orchestrator = new DatabaseMigrationOrchestrator(executor, lockProviders);

var result = await orchestrator.OrchestrateMigrationAsync(
    serviceId,
    bootstrapDatabaseConfiguration,
    lockAcquireTimeout: TimeSpan.FromSeconds(30));

if (!result.Succeeded)
{
    // Safe error code and message without exposing secrets
    logger.LogError("Migration failed: {ErrorCode}: {Message}", result.ErrorCode, result.ErrorMessage);
}
```

### PostgreSQL advisory lock

`ServiceMantle.Database.PostgreSql.Migration.PostgreSqlMigrationLockProvider` provides session-level advisory locks using `pg_try_advisory_lock` with bounded polling. The lock key is derived from the service identifier using SHA-256, ensuring stability across processes, machines, and restarts.

Lock acquisition respects both the caller-provided timeout and cancellation token. The lock is held for the lifetime of the returned lease object, and is released either explicitly (DisposeAsync) or implicitly when the connection closes.

No other lock providers (SQLite, MySQL, etc.) are implemented in this release. Multi-instance migrations without registered lock support fail closed with `migration.lock_not_supported`.

### Error codes

Safe error codes for migration failures:
- `migration.lock_not_supported` - No lock provider registered for the database.
- `migration.lock_timeout` - Lock acquisition exceeded the timeout.
- `migration.lock_failed` - Lock acquisition failed (provider-specific).
- `migration.inspection_failed` - Database state could not be determined.
- `migration.version_too_new` - Database schema is newer than the application.
- `migration.execution_failed` - The consuming service's migration executor failed.
- `migration.final_state_invalid` - Database state after migration is not compatible.

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

Standard build and test:

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release
dotnet test --solution ServiceMantle.slnx -c Release
dotnet pack src/ServiceMantle/ServiceMantle.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Database.PostgreSql/ServiceMantle.Database.PostgreSql.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Persistence.EntityFrameworkCore/ServiceMantle.Persistence.EntityFrameworkCore.csproj -c Release --no-build
```

With PostgreSQL Testcontainers (requires Docker):

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx -c Release
```

To run only PostgreSQL container tests:

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --project tests/ServiceMantle.Database.PostgreSql.Tests -c Release
```

To override PostgreSQL image:

```bash
SERVICEMANTLE_POSTGRES_IMAGE=postgres:16 RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx -c Release
```

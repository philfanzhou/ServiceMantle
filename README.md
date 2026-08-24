# ServiceMantle

ServiceMantle is a shared .NET 10 library for reusable service-management foundations used by ASP.NET Core services.

Current status: **early development**. Service identity, installation phase primitives, the instance-local Bootstrap file model, database migration orchestration, the PostgreSQL advisory lock provider, and product-agnostic management audit persistence are implementation-complete, pending CI container verification (real PostgreSQL Testcontainers run in GitHub Actions, not locally). Management authentication, observability, and service discovery capabilities are not yet implemented.

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

`service_installations` and `service_audit_logs` (see below) are defined. Planned future tables (not in this release):

- `service_settings`
- `service_data_protection_keys`
- Setup code metadata
- administrator identity/state tables

Shared migration ownership is intentionally moved to the consuming service. In deployments against existing business databases, services must create migration entries and keep installation table ownership in their own startup/deployment process.

## Management audit persistence

`ServiceMantle.Audit` (in the core `ServiceMantle` package) defines a product-agnostic management audit domain: `ManagementAuditEvent` (write contract input), `ManagementAuditRecord` (read model), `ManagementAuditQuery`/`ManagementAuditQueryResult` (query contract), and bounded value types `ManagementAuditAction`, `ManagementAuditTargetType`, `ManagementAuditTarget`, `ManagementAuditOperator`, `ManagementAuditOperatorSource`, and the `ManagementAuditOutcome` security result enum (`Unknown`/`Success`/`Failure`/`Denied`). `WellKnownManagementAuditActions`, `WellKnownManagementAuditTargetTypes`, and `WellKnownManagementAuditOperatorSources` provide reusable conventions for installation, administrator login, and configuration-change events; consuming services define additional actions and target types with the same `Parse` pattern (for example `signacore.account_created`). ServiceMantle does not define SignaCore-specific identity, application, credential, signing-key, or OAuth semantics.

`ManagementAuditEvent.Create(...)` enforces the sensitive-content policy before an event can be constructed: metadata keys must normalize to ASCII under NFKC so mixed-script confusables cannot hide a sensitive name; keys that name a secret (`password`, `passwd`, `passphrase`, `accountkey`, `privatekey`, `token`, `connectionstring`, `apikey`, `setupcode`, `authorization`, and similar) are rejected outright. When a description, display name, or metadata value contains a recognized secret assignment or database/credential-bearing URI, the entire free-text field is replaced with `[REDACTED]` so punctuation or opaque quoting cannot expose a suffix; bearer tokens, JWT-like strings, PEM private key blocks, and recognized connection strings are also redacted. Client IPs and correlation IDs use strict format allowlists; opaque operator and target identifiers that contain a supported secret-shaped format are rejected because modifying them would destroy identity semantics.

This sanitization is a defense-in-depth contract for the formats listed above, not a general-purpose data-loss-prevention engine: an opaque bare value has no intrinsic signal that distinguishes a secret from ordinary audit text. Callers **must not** place connection strings, external root keys, database administrator credentials, setup codes, passwords, tokens, or other sensitive configuration values in any audit field. Consumption-specific metadata should use an explicit non-secret allowlist before calling ServiceMantle. The persistence write guarantee applies to records staged through `EfCoreManagementAuditWriter<TDbContext>`: the writer reapplies the supported-format policy before the caller saves the shared unit of work. The mapped audit entity is internal and no writable audit `DbSet` is exposed by the package. Direct SQL, imports, and administrative database writes are outside that write guarantee; the query boundary still revalidates such legacy rows so recognized sensitive content is not returned unchanged.

`ServiceMantle.Persistence.EntityFrameworkCore` adds:

- Internal entity mapping for `service_audit_logs`; consumers do not expose a writable audit `DbSet`.
- `ModelBuilder` extension `AddServiceMantleManagementAudit(...)` for model registration. Pass the
  consuming database's `ManagementAuditDatabaseDialect` so every persisted text column is bounded by the provider's encoded-byte function and the generated constraints use valid SQL. Query pages preflight the same resource ceilings before EF materializes text, while domain validation continues to enforce the exact character and format limits.
- `EfCoreManagementAuditWriter<TDbContext>` implementing `IManagementAuditWriter`. It only stages the internal entity on the caller's configured `DbContext` — it never calls `SaveChangesAsync` and never commits a transaction. The write participates in whatever unit of work or explicit transaction the caller already owns, and future Setup/configuration flows can call it before their own `SaveChangesAsync` to persist an audit record atomically with their own changes.
- `EfCoreManagementAuditQueryService<TDbContext>` implementing `IManagementAuditQueryService`, providing bounded keyset-paginated queries filtered by action, target, operator, and time range. The first result returns an opaque `ContinuationCursor`; pass it unchanged to the immediately following `ManagementAuditQuery` rather than using an unbounded offset. The cursor is bound to the normalized filters, sort order, page size, and next page number, so it cannot be silently reused with a different query.

`TotalCount` is the count observed while each query executes and may change when rows are inserted or deleted concurrently. Continuations have ordinary keyset semantics: they avoid offset drift and repeated rows already passed in the ordering, but they do not represent a database snapshot. A concurrently inserted backfilled record whose ordering key lies after the cursor can therefore appear on a later page.

```csharp
public sealed class MyDbContext : DbContext, IServiceMantleDbContext
{
    public DbSet<ServiceInstallationEntity> ServiceInstallations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleInstallation();
        modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.PostgreSql);
    }
}

var writer = new EfCoreManagementAuditWriter<MyDbContext>(dbContext);
var auditEvent = ManagementAuditEvent.Create(
    ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, operatorId: "admin-1"),
    WellKnownManagementAuditActions.ConfigurationChanged,
    ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
    outcome: ManagementAuditOutcome.Success);

await writer.RecordAsync(auditEvent);
// ... stage other business changes on the same dbContext ...
await dbContext.SaveChangesAsync(); // caller owns save/commit
```

Authentication, admin cookies, log storage/shipping, an HTTP API surface, Setup, and shared configuration are out of scope for this layer; `service_audit_logs` rows are owned by the consuming service database, the same as `service_installations`.

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

With SQL Server Testcontainers (requires Docker on a supported Linux/AMD64 host):

```bash
RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests -c Release
```

To override the SQL Server image:

```bash
SERVICEMANTLE_SQLSERVER_IMAGE=mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04 RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests -c Release
```

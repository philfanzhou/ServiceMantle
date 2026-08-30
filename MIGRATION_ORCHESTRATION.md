# Database Migration Orchestration

This document summarizes the implementation of Provider-agnostic database migration orchestration with optional multi-instance migration lock support in ServiceMantle.

## Core Architecture

### Provider-Agnostic Core (`ServiceMantle.Migration`)

The core package defines the contract and orchestration logic without any database driver dependencies.

**Key Types:**

1. **`IDatabaseMigrationExecutor`** - Extension point for consuming services
   - `InspectAsync()` - Observe current database state (Empty, CurrentVersionCompatible, PendingMigration, VersionTooNew, InspectionFailed)
   - `ExecuteAsync()` - Execute the complete migration workflow exactly once

2. **`IDatabaseMigrationLock`** - Acquired lock lease
   - Extends `IAsyncDisposable` for RAII semantics
   - Holds the lock for its lifetime

3. **`IDatabaseMigrationLockProvider`** - Provider SPI for lock capabilities
   - `ProviderId` property to match bootstrap provider ID
   - `AcquireAsync()` - Acquire lock with timeout and cancellation support

4. **`DatabaseMigrationLockProviderRegistry`** - Case-insensitive lookup
   - Accumulates lock providers at startup
   - Rejects duplicate registrations
   - Takes the shared `DatabaseProviderIdResolver` snapshot so that registration keys and lookup
     keys are canonicalized identically, and a bootstrap provider alias finds the lock provider
     registered under the canonical id. Resolving an alias never implies lock capability: an
     unregistered capability still returns `migration.lock_not_supported`.

5. **`DatabaseMigrationOrchestrator`** - The orchestration engine
   - Implements the authority flow described below
   - Produces `MigrationExecutionResult` with safe error codes

6. **`MigrationExecutionResult`** - Safe, immutable result
   - `Succeeded` - Whether migration succeeded
   - `ErrorCode` - Well-known safe error code (if failed)
   - `ErrorMessage` - Safe message without secrets
   - `ExecutorWasCalled` - Whether the executor was invoked

7. **`WellKnownMigrationErrorCodes`** - Standard error codes
   - `migration.lock_not_supported`
   - `migration.lock_timeout`
   - `migration.lock_failed`
   - `migration.inspection_failed`
   - `migration.version_too_new`
   - `migration.execution_failed`
   - `migration.final_state_invalid`

8. **`DatabaseMigrationLockException`** - Safe lock failure exception
   - `ErrorCode` property for structured error handling
   - No connection strings or secrets in messages

### PostgreSQL Provider (`ServiceMantle.Database.PostgreSql.Migration`)

**`PostgreSqlMigrationLockProvider`** implements `IDatabaseMigrationLockProvider`:

1. **Lock Key Derivation** (`ServiceIdToLockKeyDeriver`)
   - Uses SHA-256 hash of `"ServiceMantle.Migration." + serviceId.Value`
   - Reads first 8 bytes as signed 64-bit integer (big-endian)
   - Deterministic and stable across processes, machines, and restarts
   - Not dependent on `.GetHashCode()`

2. **Lock Acquisition** with bounded polling:
   - Opens a dedicated Npgsql connection with timeout
   - Uses `pg_try_advisory_lock()` for non-blocking acquisition
   - Polls with 100ms intervals until lock acquired or deadline exceeded
   - Respects both the caller's timeout and cancellation token
   - Cancellation takes precedence over timeout

3. **Lock Lease** (`PostgreSqlMigrationLock`):
   - Holds an open connection for the lock lifetime
   - On `DisposeAsync()`:
     - Attempts explicit `pg_advisory_unlock()` if connection is open
     - Closes connection (session lock released by PostgreSQL)
     - Suppresses any errors to avoid masking primary exceptions

## Orchestration Flow

**The authority flow is:**

1. **Parameter validation** - Check cancellation immediately
2. **Lock resolution** - Find and acquire provider-specific lock
   - Fail closed if no lock provider registered (security boundary)
   - Fail closed on timeout or cancellation
3. **Authority inspection** - Re-check state under the lock
4. **Decision tree**:
   - If `CurrentVersionCompatible` → Skip execution, return success
   - If `VersionTooNew` → Fail closed, do not execute
   - If `InspectionFailed` → Fail closed, do not execute
   - If `Empty` or `PendingMigration` → Call executor exactly once
5. **Authority re-inspection** - Check state after execution
   - Success only if final state is `CurrentVersionCompatible`
6. **Lock release** - Always in finally block, errors suppressed

## Multi-Instance Behavior

When two instances attempt migration to the same database:

1. **Instance A** acquires the lock first
2. **Instance B** waits during lock acquisition (polling with timeout)
3. **Instance A** inspects, sees `PendingMigration`, calls executor, re-inspects, succeeds
4. **Instance A** releases the lock in finally block
5. **Instance B** finally acquires the lock
6. **Instance B** inspects, sees `CurrentVersionCompatible` (due to Instance A's work)
7. **Instance B** skips execution and returns success
8. **Instance B** releases the lock

Both instances report success, but only Instance A executed migrations. No duplicate execution or silent failures.

## Security Boundaries

### Error Codes

All migration failures produce safe, well-known error codes that:
- Do not expose connection strings, passwords, or internal details
- Can be logged and displayed safely
- Are usable for structured error handling in consuming services

### Exception Messages

`DatabaseMigrationLockException` and `MigrationExecutionResult` messages:
- Never contain connection strings or authentication details
- Classify errors by `ErrorCode` only
- Provider exceptions are caught and re-wrapped with safe classification

### Lock Secrets

The lock key is:
- Derived deterministically from ServiceId
- Never logged or exposed
- The same across all invocations of the same ServiceId
- Different for different ServiceIds (no cross-service contention)

## Testing and Validation Status

**Implementation complete, pending CI container verification.** All unit and in-memory concurrency tests pass locally. The real PostgreSQL Testcontainers suite requires Docker and does not run on this development machine; it is exercised by GitHub Actions on every pull request and before release (see `.github/workflows/ci.yml`, step "Test (PostgreSQL with containers)"). This document will be updated to reflect accepted status once that CI run has passed on this branch. Run `dotnet test --solution ServiceMantle.slnx` for current pass/fail/skip counts rather than relying on numbers recorded here, since counts drift as tests are added.

### Unit and in-memory tests (verified locally)

**`ServiceMantle.Tests.Migration`:**
- `DatabaseMigrationOrchestratorTests` - Core orchestration logic, covering:
  - Current-version skip, empty/pending-migration execution, version-too-new fail-closed
  - Initial inspection failure, execution failure, final-state validation failure
  - Cancellation before start and cancellation during execution (both leave the lease released exactly once)
  - Lock timeout, lock-not-supported, and null-lease fail-closed paths
  - Lease release count for every success and failure path (via `FakeMigrationLockProvider.LeaseDisposeCount`)
  - Double-instance scenario with shared in-memory state (only one instance executes)
- `DatabaseMigrationLockProviderRegistryTests` - registry lookup, case-insensitivity, duplicate/null rejection
- `ProviderIdCanonicalizationTests` - alias-to-canonical resolution across persistence, provider dispatch, target preparation, and lock lookup

**`ServiceMantle.Database.PostgreSql.Tests.Migration`:**
- `ServiceIdToLockKeyDeriverTests` - lock key derivation is deterministic, differs per ServiceId, matches fixed SHA-256 vectors, and rejects null input

### Real PostgreSQL tests (Testcontainers, require Docker, run in GitHub Actions CI)

**`PostgreSqlMigrationLockConcurrencyTests`** is enabled via environment variable:

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --project tests/ServiceMantle.Database.PostgreSql.Tests/ServiceMantle.Database.PostgreSql.Tests.csproj
```

Optional image override:
```bash
SERVICEMANTLE_POSTGRES_IMAGE=postgres:16 RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx
```

**Advisory lock tests against a real PostgreSQL container:**
- Same ServiceId uses same lock key; different ServiceIds use different keys
- Second instance blocks on acquisition and only proceeds after the first releases
- Different ServiceIds do not contend: `Lock_DifferentServiceIds_DontCompete` holds service-a's lease open and acquires service-b's lease with a short bounded timeout — if the two ServiceIds incorrectly mapped to the same advisory lock key, this acquisition would time out and fail the test deterministically
- Lock acquisition respects timeout and fails safely with `LockTimeout`
- Cancellation during polling throws (`OperationCanceledException` or `TaskCanceledException`)
- Lock release allows re-acquisition
- No secrets (passwords, connection strings) in exception messages

**End-to-end orchestration test against a real PostgreSQL container:**
- `OrchestratorDoubleInstance_OnlyOneExecutes_ViaAdvisoryLock` runs two orchestrator instances concurrently against the same ServiceId and a real `test_migration_state` table. A shared gate (`TaskCompletionSource`, `RunContinuationsAsynchronously`) holds the winning executor inside `ExecuteAsync` — with the advisory lock still held, verified by a bounded probe acquisition that must time out — until the second orchestrator's own acquisition attempt has started. Both executors share the same gate, so if the advisory lock failed to provide mutual exclusion, both would reach `ExecuteAsync` and, once released, race to increment `execution_count` concurrently. The test asserts both orchestrators succeed, exactly one reports `ExecutorWasCalled`, `execution_count` is exactly 1, and the final state is `current` — making the assertions fail deterministically if locking is broken, rather than passing by timing coincidence.

**Test infrastructure:**
- Testcontainers PostgreSQL (image configurable via `SERVICEMANTLE_POSTGRES_IMAGE`, default `postgres:15-alpine`) with automatic lifecycle management
- Real test database with a migration-state table created and dropped per orchestration test
- Real `PostgreSqlMigrationLockProvider` using PostgreSQL advisory locks (no fake/in-memory locking in these tests)
- Real `DatabaseMigrationOrchestrator` orchestrating both instances

## Limitations and Future Work

### Current Scope (Implemented)

- PostgreSQL advisory lock (session-level, ACID)
- Multi-instance safe orchestration
- Deterministic lock key derivation
- Timeout and cancellation support
- Structured safe error handling
- Comprehensive unit and concurrency tests

### Out of Scope (Not Implemented)

- Other database providers (MySQL, MariaDB, Oracle, SQL Server, SQLite)
- Database creation or target preparation (see the separate "Database target preparation" section in `README.md`, added independently of this migration orchestration work)
- Configuration tables or audit tables
- Setup code or management admin features
- EF Core automatic migration execution
- Break-glass/emergency unlock procedures

### Future Provider Support

When additional providers are needed:
1. Implement `IDatabaseMigrationLockProvider` in provider package
2. Register provider instance in `DatabaseMigrationLockProviderRegistry`
3. Provider must support timeout and cancellation semantics
4. Use deterministic lock key derivation aligned with PostgreSQL pattern

For SQLite, an explicit single-instance mode should be documented rather than a silent no-op lock.

## Integration Example

```csharp
// 1. Implement the executor (consuming service responsibility)
public sealed class MyServiceMigrationExecutor : IDatabaseMigrationExecutor
{
    private readonly MyDbContext dbContext;

    public MyServiceMigrationExecutor(MyDbContext dbContext)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
    {
        // Check current schema version against application expectations
        // Return Empty, CurrentVersionCompatible, PendingMigration, VersionTooNew, or InspectionFailed
        var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet();

        if (appliedMigrations.Count == 0 && !await HasBusinessDataAsync(cancellationToken))
        {
            return MigrationObservationState.Empty;
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count == 0)
        {
            return MigrationObservationState.CurrentVersionCompatible;
        }

        return MigrationObservationState.PendingMigration;
    }

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Execute complete migration workflow
        // This may include EF Core migrations, expand-contract, backfill, validation, etc.
        await dbContext.Database.MigrateAsync(cancellationToken);
        // ... additional business migration steps
    }

    private async ValueTask<bool> HasBusinessDataAsync(CancellationToken cancellationToken)
    {
        // Check if database contains meaningful business data
        return await dbContext.Accounts.AnyAsync(cancellationToken)
            || await dbContext.Orders.AnyAsync(cancellationToken);
    }
}

// 2. Register lock provider and create orchestrator
var lockProviders = new DatabaseMigrationLockProviderRegistry(
    [new PostgreSqlMigrationLockProvider()],
    providerRegistry.ProviderIdResolver);

var executor = new MyServiceMigrationExecutor(dbContext);
var orchestrator = new DatabaseMigrationOrchestrator(executor, lockProviders);

// 3. Orchestrate migration
var result = await orchestrator.OrchestrateMigrationAsync(
    serviceId,
    bootstrapConfiguration.Database,
    lockAcquireTimeout: TimeSpan.FromSeconds(30),
    cancellationToken: cts.Token);

if (!result.Succeeded)
{
    logger.LogError(
        "Database migration failed: {ErrorCode}: {ErrorMessage}",
        result.ErrorCode,
        result.ErrorMessage);
    return;
}

logger.LogInformation(
    "Database migration completed. Executor was called: {ExecutorWasCalled}",
    result.ExecutorWasCalled);
```

## Files Changed

### Core Package

**New files:**
- `src/ServiceMantle/Migration/IDatabaseMigrationExecutor.cs` - Extension point
- `src/ServiceMantle/Migration/IDatabaseMigrationLock.cs` - Lock lease interface
- `src/ServiceMantle/Migration/IDatabaseMigrationLockProvider.cs` - Lock provider SPI
- `src/ServiceMantle/Migration/DatabaseMigrationLockProviderRegistry.cs` - Provider registry
- `src/ServiceMantle/Migration/DatabaseMigrationOrchestrator.cs` - Orchestration engine
- `src/ServiceMantle/Migration/MigrationExecutionResult.cs` - Safe result model
- `src/ServiceMantle/Migration/DatabaseMigrationLockException.cs` - Safe exception
- `src/ServiceMantle/Migration/WellKnownMigrationErrorCodes.cs` - Error code constants

### PostgreSQL Provider

**New files:**
- `src/ServiceMantle.Database.PostgreSql/Migration/PostgreSqlMigrationLockProvider.cs`
- `src/ServiceMantle.Database.PostgreSql/Migration/PostgreSqlMigrationLock.cs`
- `src/ServiceMantle.Database.PostgreSql/Migration/ServiceIdToLockKeyDeriver.cs`

**Modified files:**
- `src/ServiceMantle.Database.PostgreSql/ServiceMantle.Database.PostgreSql.csproj` - Updated description and tags

### Core Tests

**New files:**
- `tests/ServiceMantle.Tests/Migration/DatabaseMigrationOrchestratorTests.cs`
- `tests/ServiceMantle.Tests/Migration/DatabaseMigrationLockProviderRegistryTests.cs`
- `tests/ServiceMantle.Tests/Migration/FakeMigrationExecutor.cs` - Test double
- `tests/ServiceMantle.Tests/Migration/FakeMigrationLockProvider.cs` - Test double

### PostgreSQL Tests

**New files:**
- `tests/ServiceMantle.Database.PostgreSql.Tests/Migration/ServiceIdToLockKeyDeriverTests.cs`
- `tests/ServiceMantle.Database.PostgreSql.Tests/Migration/PostgreSqlMigrationLockConcurrencyTests.cs`

**Modified files:**
- `tests/ServiceMantle.Database.PostgreSql.Tests/ServiceMantle.Database.PostgreSql.Tests.csproj` - Added Testcontainers

### Configuration and Documentation

**Modified files:**
- `Directory.Packages.props` - Added Testcontainers packages
- `README.md` - Added migration orchestration section
- `MIGRATION_ORCHESTRATION.md` - This document

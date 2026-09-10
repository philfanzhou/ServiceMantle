# Database Migration Orchestration

This document summarizes the implementation of Provider-agnostic database migration orchestration with optional multi-instance migration lock support in ServiceMantle.

## Core Architecture

### Provider-Agnostic Core (`ServiceMantle.Migration`)

The core package defines the contract and orchestration logic without any database driver dependencies.

**Key Types:**

1. **`IDatabaseMigrationExecutor`** - Extension point for consuming services
   - `InspectAsync()` - Observe current database state (Empty, CurrentVersionCompatible, PendingMigration, VersionTooNew, InspectionFailed). It is read-only; the consuming service owns what it reads and what it refuses
   - `ExecuteAsync()` - Run the consuming service's migration workflow. The orchestrator calls it **at most once per orchestration**, and only when the inspection under authority returned `Empty` or `PendingMigration`. A `CurrentVersionCompatible` target skips it entirely, so an orchestration that succeeds without calling the executor is the normal outcome for an already-current database

2. **`IDatabaseMigrationLock`** - Acquired lock lease
   - Extends `IAsyncDisposable` for RAII semantics
   - Holds the lock for its lifetime
   - Exposes a permanent `LeaseLost` cancellation signal when the provider detects lost authority

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
   - Probes the dedicated connection every 250ms with a one-second command timeout
   - Signals detected session loss within a conservative five-second running-process bound
   - On `DisposeAsync()`:
     - Attempts explicit `pg_advisory_unlock()` if connection is open
     - Closes connection (session lock released by PostgreSQL)
     - Suppresses any errors to avoid masking primary exceptions

### Oracle Provider (`ServiceMantle.Database.Oracle.Migration`)

**`OracleMigrationLockProvider`** implements `IDatabaseMigrationLockProvider`:

1. It derives `ServiceMantle.Migration.` plus the full lowercase SHA-256 digest of the normalized
   `ServiceId`, avoiding the collision-prone caller-assigned numeric lock-ID range.
2. It opens a dedicated target-user session with pooling and ambient enlistment disabled, validates
   the supported runtime topology, allocates the handle with
   `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS`, and requests `X_MODE` using the remaining bounded timeout
   and `release_on_commit => FALSE`.
3. A missing direct `EXECUTE ON SYS.DBMS_LOCK` grant maps to `migration.lock_not_supported`;
   `REQUEST` code 1 maps to `migration.lock_timeout`; codes 2 through 5 and other operational
   failures map to `migration.lock_failed`; caller cancellation remains `OperationCanceledException`.
4. The acquired lease probes its dedicated session every 250 milliseconds with a one-second command
   timeout and uses the provider-neutral `LeaseLost` signal. Disposal explicitly calls
   `DBMS_LOCK.RELEASE` and then closes the unpooled session.

## Orchestration Flow

**The authority flow is:**

1. **Parameter validation** - Check cancellation immediately
2. **Lock resolution** - Find and acquire provider-specific lock
   - Fail closed if no lock provider registered (security boundary)
   - Fail closed on timeout or cancellation
3. **Authority inspection** - Re-check state under the lock while monitoring `LeaseLost`
4. **Decision tree**:
   - If `CurrentVersionCompatible` → Skip execution, return success
   - If `VersionTooNew` → Fail closed, do not execute
   - If `InspectionFailed` → Fail closed, do not execute
   - If `Empty` or `PendingMigration` → Call the executor once, and only in this branch
5. **Authority re-inspection** - Check state after execution under the same monitored lease
   - Success only if final state is `CurrentVersionCompatible`
6. **Lock release** - Always in finally block, errors suppressed

`ExecuteAsync` is therefore called at most once per orchestration, and not at all when the target is
already compatible or when the observation fails closed. It is not "exactly once" in any global
sense: repeating an orchestration against a target that still needs migration calls it again.

Every executor call receives a token linked to caller cancellation and `LeaseLost`. The orchestrator
checks caller cancellation first before and after every stage, so a caller-cancellation/lease-loss
race remains `OperationCanceledException`. Lease loss maps to `migration.lock_failed`, records whether
execution had started, and prevents a not-yet-started next stage. Executors must observe the supplied
token promptly; loss detection cannot roll back side effects already committed by an executor.

### The two `OrchestrateMigrationAsync` overloads

`DatabaseMigrationOrchestrator` exposes two overloads, and neither degrades into the other.

- **`(serviceId, bootstrap, lockAcquireTimeout, cancellationToken)`** always requires a real
  distributed lease. A missing lock provider is not a reason to continue: it fails closed with
  `migration.lock_not_supported`. This overload never consults a deployment declaration.
- **`(serviceId, bootstrap, deploymentMode, lockAcquireTimeout, cancellationToken)`** validates the
  consumer-supplied `DatabaseDeploymentMode` against the declared capabilities first. `MultiInstance`
  runs exactly the flow above, real lease included. `SingleInstance` instead resolves the provider's
  canonical target identity and serializes calls for that provider/target **within this process**;
  it constructs no `IDatabaseMigrationLock`. `Unspecified`, an undefined mode, a provider with no
  declared capability, or `SingleInstance`-only capability asked for `MultiInstance` all fail closed
  with `migration.lock_not_supported`. This overload requires the three-argument constructor that
  takes a `DatabaseDeploymentCapabilityRegistry`; used with the two-argument constructor it fails
  closed as well.

An absent lock provider never causes an automatic fall back to `SingleInstance`. The mode is a
consumer decision, not something inferred from the registered providers or from the connection
string - see
[Explicit database deployment mode](README.md#explicit-database-deployment-mode) in `README.md`.
Process-local serialization is not a cross-process lock and is not proof of deployment topology: two
processes both configured as `SingleInstance` are neither detected nor coordinated.

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

The unit and in-memory concurrency tests run in the normal solution suite. The real PostgreSQL
Testcontainers suite requires Docker; it can be enabled locally and is exercised by GitHub Actions
on every pull request and before release (see `.github/workflows/ci.yml`). Run
`dotnet test --solution ServiceMantle.slnx` for current pass/fail/skip counts rather than relying on
numbers recorded here, since counts drift as tests are added.

### Unit and in-memory tests (verified locally)

**`ServiceMantle.Tests.Migration`:**
- `DatabaseMigrationOrchestratorTests` - Core orchestration logic, covering:
  - Current-version skip, empty/pending-migration execution, version-too-new fail-closed
  - Initial inspection failure, execution failure, final-state validation failure
  - Cancellation before start and cancellation during execution (both leave the lease released exactly once)
  - Lock timeout, lock-not-supported, and null-lease fail-closed paths
  - Lease release count for every success and failure path (via `FakeMigrationLockProvider.LeaseDisposeCount`)
  - Double-instance scenario with shared in-memory state (only one instance executes)
  - Lease loss during initial inspection, execution, and final inspection
  - Caller cancellation priority when it races with lease loss
- `DatabaseMigrationLockProviderRegistryTests` - registry lookup, case-insensitivity, duplicate/null rejection
- `ProviderIdCanonicalizationTests` - alias-to-canonical resolution across persistence, provider dispatch, target preparation, and lock lookup

**`ServiceMantle.Database.PostgreSql.Tests.Migration`:**
- `ServiceIdToLockKeyDeriverTests` - lock key derivation is deterministic, differs per ServiceId, matches fixed SHA-256 vectors, and rejects null input

**`ServiceMantle.Database.Oracle.Tests.Migration`:**
- `OracleMigrationLockProviderTests` - full-digest fixed vectors, target-session isolation, timeout
  and caller cancellation precedence, every `REQUEST` return-code mapping, missing direct privilege,
  safe failures, explicit release, cleanup, and lease-loss signalling

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
- Deterministic `pg_terminate_backend` of the holding session during execution, proving that the
  orchestrator returns `migration.lock_failed` within the five-second detection bound and does not
  begin final inspection

**End-to-end orchestration test against a real PostgreSQL container:**
- `OrchestratorDoubleInstance_OnlyOneExecutes_ViaAdvisoryLock` runs two orchestrator instances concurrently against the same ServiceId and a real `test_migration_state` table. A shared gate (`TaskCompletionSource`, `RunContinuationsAsynchronously`) holds the winning executor inside `ExecuteAsync` — with the advisory lock still held, verified by a bounded probe acquisition that must time out — until the second orchestrator's own acquisition attempt has started. Both executors share the same gate, so if the advisory lock failed to provide mutual exclusion, both would reach `ExecuteAsync` and, once released, race to increment `execution_count` concurrently. The test asserts both orchestrators succeed, exactly one reports `ExecutorWasCalled`, `execution_count` is exactly 1, and the final state is `current` — making the assertions fail deterministically if locking is broken, rather than passing by timing coincidence.

**Test infrastructure:**
- Testcontainers PostgreSQL (image configurable via `SERVICEMANTLE_POSTGRES_IMAGE`, default `postgres:15-alpine`) with automatic lifecycle management
- Real test database with a migration-state table created and dropped per orchestration test
- Real `PostgreSqlMigrationLockProvider` using PostgreSQL advisory locks (no fake/in-memory locking in these tests)
- Real `DatabaseMigrationOrchestrator` orchestrating both instances

### Real Oracle tests (pinned FREEPDB1, require the shared Oracle environment)

`OracleMigrationLockRealDatabaseTests` uses the hard-fail environment registered in
`eng/packages.json`. It proves same-service exclusion, different-service independence,
release/reacquire and unpooled connection cleanup, bounded timeout, caller cancellation, direct
package-permission denial, termination before acquisition, deterministic termination during initial
inspection/execution/final inspection, and two-orchestrator lock-held recheck with exactly one real
state update. CI and ReleaseTool require the environment, fail on missing variables, skips, zero
discovered tests, container or connection failure, and use the ADR-pinned Oracle Database Free image.

## Limitations and Future Work

### Current Scope (Implemented)

- Migration lock providers for five database products, each documented with its own key derivation,
  acquisition, lease-probing and release semantics in `README.md`:
  [PostgreSQL advisory lock](README.md#postgresql-advisory-lock),
  [Oracle `DBMS_LOCK`](README.md#oracle-dbms_lock),
  [MySQL named lock](README.md#mysql-named-lock),
  [MariaDB named lock](README.md#mariadb-named-lock), and
  [SQL Server application lock](README.md#sql-server-application-lock)
- Multi-instance safe orchestration
- Explicit deployment-mode validation and process-local `SingleInstance` serialization
- Deterministic lock key derivation
- Timeout and cancellation support
- Structured safe error handling
- Comprehensive unit and concurrency tests
- Lease-loss detection during all orchestration stages

### Out of Scope (Not Implemented)

- A migration lock provider for SQLite. SQLite has **no cross-process migration lock** in this
  repository. It participates only through the explicit `SingleInstance` deployment mode, which
  serializes migrations inside one process and is not a claim of multi-instance support; asking for
  `MultiInstance` on SQLite fails closed with `migration.lock_not_supported`
- Database creation or target preparation (see the separate "Database target preparation" section in `README.md`, added independently of this migration orchestration work)
- Configuration tables or audit tables
- Setup code or management admin features
- EF Core automatic migration execution
- Break-glass/emergency unlock procedures
- Fencing tokens or automatic rollback of executor side effects committed before lease loss

Delivered lock support is per product and per documented boundary. It is not a claim that every
provider offers equivalent topology support, nor that any of them is production-ready for a given
deployment.

The five-second PostgreSQL bound assumes a running process with normally scheduled timers and a
working Npgsql command-timeout mechanism. Process suspension, severe scheduler starvation, and a
runtime or network stack that cannot deliver the configured timeout are explicit non-guarantees.

### Future Provider Support

When additional providers are needed:
1. Implement `IDatabaseMigrationLockProvider` in provider package
2. Register provider instance in `DatabaseMigrationLockProviderRegistry`
3. Provider must support timeout and cancellation semantics
4. Use deterministic lock key derivation aligned with PostgreSQL pattern

SQLite keeps the explicit single-instance route rather than a silent no-op lock: the consumer states
`SingleInstance` and owns that deployment assumption. A no-op `IDatabaseMigrationLockProvider` that
pretends to hold a lease must not be added.

## Integration Example

This is a **composition** example. It shows how a consuming service hands its own executor to the
orchestrator; it deliberately does not show how to write that executor.

Deciding whether an unknown database is safe to adopt is the consuming service's own problem, and it
cannot be answered generically. In particular, "`GetPendingMigrations()` is empty" does not mean the
target is compatible - a database holding a migration id this build has never heard of also reports
no pending migrations - and "the business tables are empty" is not permission to take over a schema
somebody else created. An executor must decide from evidence it can actually read, and refuse what
it cannot classify.

`IDatabaseMigrationExecutor` is documented in
[Database migration orchestration](README.md#database-migration-orchestration) in `README.md`:
`InspectAsync` is read-only and returns one of the five finite `MigrationObservationState` values,
`ExecuteAsync` runs the consuming service's own workflow, and both receive a token linked to caller
cancellation and `LeaseLost` that they must observe promptly. Cancellation cannot roll back side
effects the executor has already committed.

For a worked example of a finite, conservative observation over a schema a service really owns, see
the reference sample's consumer-owned SQLite executor,
`samples/ServiceMantle.ReferenceService/Database/Sqlite/ReferenceSqliteMigrationExecutor.cs`, and
[its acceptance notes](docs/testing/reference-sqlite-deployment.md). Its rules are specific to that
sample's schema; they are an illustration, not a library algorithm to copy blindly.

```csharp
// 1. The consuming service supplies its own IDatabaseMigrationExecutor implementation.
//    It owns its schema, its history, its finite observation rules, and its transactions.
IDatabaseMigrationExecutor executor = myServiceMigrationExecutor;

// 2. Register the lock provider for the database product in use and build the orchestrator.
var lockProviders = new DatabaseMigrationLockProviderRegistry(
    [new PostgreSqlMigrationLockProvider()],
    bootstrapProviders.ProviderIdResolver);

var orchestrator = new DatabaseMigrationOrchestrator(executor, lockProviders);

// 3. Orchestrate. This overload always requires a real distributed lease; a missing lock provider
//    fails closed rather than continuing without one.
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

// A successful orchestration against an already-compatible target reports ExecutorWasCalled = false.
logger.LogInformation(
    "Database migration completed. Executor was called: {ExecutorWasCalled}",
    result.ExecutorWasCalled);
```

To let a consumer state `SingleInstance` instead, use the deployment-aware constructor and overload
described in [Explicit database deployment mode](README.md#explicit-database-deployment-mode).

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

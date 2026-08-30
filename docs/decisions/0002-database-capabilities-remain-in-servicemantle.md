# ADR 0002: Database capabilities remain in ServiceMantle

- Status: Accepted
- Date: 2026-08-30
- Decision issue: [#179](https://github.com/philfanzhou/ServiceMantle/issues/179)
- Re-evaluation evidence: [#65](https://github.com/philfanzhou/ServiceMantle/issues/65), [PR #206](https://github.com/philfanzhou/ServiceMantle/pull/206)

## Context

ServiceMantle and [CoddLoom](https://github.com/philfanzhou/CoddLoom) both have
database-provider packages. That overlap could suggest moving ServiceMantle's database
capabilities into CoddLoom and sharing its provider matrix.

The overlap is narrower than the package names imply. ServiceMantle has four distinct
database responsibilities:

1. observing a configured target and explicitly preparing a missing target;
2. coordinating migrations with a provider-specific lock;
3. persisting ServiceMantle-owned records in a consumer-owned database; and
4. validating Bootstrap database configuration.

Only the third responsibility is an ORM integration concern. The other three operate at
connection, server, or orchestration boundaries that CoddLoom's ORM abstraction does not
model.

## Decision

ServiceMantle keeps these capabilities in this repository. Each optional provider package
references its ADO.NET driver directly. The provider-agnostic contracts remain in the core
package, while driver-specific implementation remains in the corresponding optional provider
package.

The existing EF Core persistence package remains an optional adapter. It is not replaced by
CoddLoom, and the core package remains independent of both ORMs.

### Target observation, preparation, and Bootstrap validation

`IDatabaseTargetPreparationProvider` and `IBootstrapDatabaseProvider` are asynchronous and
receive a `CancellationToken` for every operation. Observation must turn connection failures
into safe structured outcomes instead of allowing a connection-opening exception to escape.
Preparation also has provider-specific responsibilities outside an ORM's table-access layer:

- parse and validate target and administrative connection information;
- distinguish server, authentication, permission, target-existence, and target-conflict
  outcomes;
- isolate privileged connections from pooling and ambient consumer transactions where the
  provider requires it;
- quote provider identifiers safely for target-creation DDL; and
- preserve the non-destructive rule for an existing target.

CoddLoom's `DbExecutor` at the measured revision is synchronous and opens a connection in its
constructor. It therefore does not provide ServiceMantle's asynchronous cancellation and safe
observation boundary. Its builders operate on tables inside an already selected database; they
do not replace server-level target preparation.

### Migration locking

`IDatabaseMigrationLockProvider` acquires a provider-specific lease asynchronously with a
bounded timeout and caller cancellation. The required primitives are provider-specific, such as
PostgreSQL session advisory locks, SQL Server application locks, MySQL named locks, or Oracle
`DBMS_LOCK`. Those primitives have different naming, lifetime, timeout, cancellation, and
failure mappings.

CoddLoom has no migration-lock contract. Adapting its synchronous executor would not remove the
provider-specific lock logic and would make cancellation depend on blocking a worker thread.
ServiceMantle therefore keeps lock providers next to its migration orchestration contracts.

### Persistence

`ServiceMantle.Persistence.EntityFrameworkCore` exists so a consuming EF Core service can add
ServiceMantle mappings to its own `DbContext` and generate one migration history. The consumer
retains ownership of that `DbContext`, transaction, and migration execution; an adapter operation
saves or only stages changes exactly as its public contract declares. ServiceMantle core exposes
provider-independent persistence contracts; EF Core is an adapter rather than a core dependency.

Replacing that adapter with CoddLoom would give an EF Core consumer two schema and persistence
stacks for one business database. It would also break the intended integration model in which
the consumer owns the shared `DbContext` and its migrations. A future CoddLoom persistence
adapter may be added alongside the EF Core adapter if a real consumer requires one.

### Package boundaries

This decision preserves the following dependency direction:

- `ServiceMantle` contains provider- and ORM-agnostic contracts;
- `ServiceMantle.AspNetCore` contains hosting and registration integration without a database
  driver dependency;
- each `ServiceMantle.Database.*` package references the core package and its ADO.NET driver;
  and
- each `ServiceMantle.Persistence.*` package is an optional adapter for one persistence stack.

CoddLoom is not added to the package graph merely to share a few provider-specific connection
string or identifier helpers.

CoddLoom core targets `netstandard2.0` and its Oracle package targets `netstandard2.1` at the
measured revision, while ServiceMantle targets .NET 10. Moving ServiceMantle contracts downward
would either reduce their target and API surface or make CoddLoom carry a downstream-driven
multi-targeting requirement. Neither trade is justified by the measured reuse.

## Measured re-evaluation

The Oracle provider decision in #65 tested the estimate that CoddLoom would remove little
provider code. The inspection used CoddLoom commit
[`f23f611`](https://github.com/philfanzhou/CoddLoom/tree/f23f611cda9afaa0eef12cf644af75c3e916de9f).

At that revision:

- `OracleExecutor` accepts a complete caller-provided connection string and delegates to the
  synchronous `DbExecutor`;
- `OracleBuilder` generates ORM table SQL from caller-provided identifiers and does not expose a
  reusable Oracle identifier-quoting contract;
- the Oracle package does not provide a connection-string builder abstraction; and
- CoddLoom does not provide a migration-lock abstraction.

An Oracle ServiceMantle provider would still need to use ODP.NET directly for connection-string
parsing, identity validation, safe administrative DDL, Oracle error classification, privileged
connection isolation, and `DBMS_LOCK`. Adding CoddLoom would retain that work while introducing an
ORM dependency.

This is the first measured non-PostgreSQL provider result requested by #179. It confirms the
decision, so no dependency-migration issue is required.

## Consequences

- Provider packages may repeat a small amount of connection-string and identifier handling.
  That duplication is accepted because the safety rules and failure classifications remain
  provider-specific.
- ServiceMantle can preserve asynchronous cancellation and safe structured failures without
  adapting a synchronous ORM executor.
- ServiceMantle's server-creation and migration-lock privileges are not added to CoddLoom's
  dependency graph.
- EF Core consumers continue to own one `DbContext`, one migration history, and their transaction
  boundaries.
- Adding another provider requires a new optional provider package and its direct driver
  dependency; it does not change the core package boundary.

## Explicit non-guarantees

- This decision does not claim that every future provider will always be cheaper to implement
  directly. It records the current architecture and one measured Oracle result.
- It does not evaluate CoddLoom's implementation quality, correctness, or performance.
- It does not prohibit a future `ServiceMantle.Persistence.CoddLoom` adapter. Such an adapter
  would be additive and consumer-driven rather than a relocation of existing capabilities.
- It does not guarantee that EF Core is appropriate for every future consumer. The current EF
  adapter remains optional.
- It does not standardize connection-string parsing, identifier quoting, error classification,
  or lock semantics across providers beyond ServiceMantle's existing public contracts.
- It does not move target preparation, migration locking, persistence, or Bootstrap validation
  code, and it does not change any runtime behavior.

## Re-evaluation triggers

Re-open #179 instead of making a provider-local architecture exception when any of the following
occurs:

- CoddLoom provides asynchronous execution and no longer opens a connection in the executor
  constructor;
- a ServiceMantle consumer that does not use EF Core needs shared persistence integration;
- an implemented provider demonstrates that shared connection-string, identifier, or capability
  handling is materially larger than the Oracle measurement; or
- ServiceMantle needs a provider already covered by CoddLoom and the measured implementation cost
  is materially greater than the cross-repository dependency cost.

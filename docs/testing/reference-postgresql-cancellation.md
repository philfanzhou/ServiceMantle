# Reference service PostgreSQL executor cancellation

`ReferencePostgreSqlMigrationExecutor` is the reference sample's consumer-owned schema boundary for
PostgreSQL. This document describes one property of it: when a caller's cancellation is observed, and
what the executor promises - and refuses to promise - about it.

Nothing here wires the sample's host to PostgreSQL, and a schema this executor calls compatible is
still not an installation that is `Completed` or `Ready`. See
[reference-postgresql-migrations.md](reference-postgresql-migrations.md) for the finite schema matrix.

## The finalisation checkpoint

Both public entry points converge on the same rule:

1. The call reads the caller's token before it starts. A cancellation already requested at that point
   means no connection is opened and no migration is started.
2. The call does its own work - the read-only observation, or the one explicit `MigrateAsync`.
3. The resources this call owns are released. For the observation that is its own `NpgsqlConnection`
   and reader; for the execution it is whatever EF Core opened for the migration.
4. **Only then** is the caller's token read one last time. If cancellation has been requested by that
   checkpoint, the call throws an `OperationCanceledException` whose `CancellationToken` is the
   caller's own token.

The checkpoint outranks whatever the call had already computed. A finite observation that was decided
before the connection was released is not returned, and a migration that ran to normal completion is
not reported as a success. `InspectAsync` and `ExecuteAsync` each converge on their own checkpoint;
neither borrows the other's.

## Failures, and cancellations that are not the caller's

| Situation | Result |
| --- | --- |
| Observation fails, caller has not cancelled | `MigrationObservationState.InspectionFailed` |
| Observation fails, caller has cancelled | `OperationCanceledException` with the caller's token |
| Observation throws an `OperationCanceledException` for some other token, caller has not cancelled | `InspectionFailed` |
| Observation completes with any finite state, caller has cancelled | `OperationCanceledException` with the caller's token |
| Execution fails, caller has not cancelled | `ReferencePostgreSqlMigrationFailedException` |
| Execution fails, caller has cancelled | `OperationCanceledException` with the caller's token |
| Execution throws an `OperationCanceledException` for some other token, caller has not cancelled | `ReferencePostgreSqlMigrationFailedException` |
| Execution completes normally, caller has cancelled | `OperationCanceledException` with the caller's token |

An internal cancellation is never re-labelled as the caller's. `ReferencePostgreSqlMigrationFailedException`
keeps its fixed message, carries no inner exception, and carries no provider text, so a connection
secret or a server message cannot reach a diagnostic through it.

## What this does not guarantee

- **The window after the checkpoint.** A cancellation requested while the result is already travelling
  back to the caller is not caught. The checkpoint is a boundary, not a continuous guard.
- **Interrupting uncooperative I/O.** Cancellation is passed to the provider and observed at the
  checkpoint. It does not forcibly abort a driver call that ignores it, and it puts no upper bound on
  how long a call takes to return.
- **Undoing DDL.** A cancelled execution is not a rolled-back one. It says nothing about how much of
  the migration was already committed, and nothing recovers a process that was killed mid-migration.
- **Anything across calls.** The executor takes no lock, holds nothing between calls, and cannot stop
  external DDL between an observation and an execution. The caller still owns serialization, the
  authorization of the target, the context, every save, and every transaction.
- **Third-party logs.** The negative assertions cover this executor's own results, exceptions, and
  their `ToString()`. They say nothing about raw EF Core or Npgsql logging the caller has enabled.

## How it is covered

`ReferencePostgreSqlCancellationCompletionTests` covers the checkpoint deterministically, without a
database:

- The **execution** boundary is driven through EF Core's own public extensibility. The test builds
  the context over an explicit internal service provider and puts its own `IMigrator` in it, so
  `MigrateAsync` can complete normally, fail, or cancel the caller's source at a chosen moment. The
  connection string is unreachable on purpose: no case in that file contacts a server.
- The **observation** boundary is driven through a controlled observation. The executor has a
  constructor overload that takes the read-only observation as an operation:

  ```csharp
  var executor = new ReferencePostgreSqlMigrationExecutor(context, async _ =>
  {
      await using var release = new CancelOnRelease(callerSource);
      return MigrationObservationState.CurrentVersionCompatible;
  });
  ```

  The finite result is computed first and the release runs after it, which is the ordering the
  database-backed observation has when it closes its connection and reader before returning. That
  overload exists for this boundary only. It does not change what the database-backed observation
  reads, the connection-string constructors remain the production entry point, and no library SPI,
  `InternalsVisibleTo`, or cross-provider helper was added for it.

`ReferencePostgreSqlMigrationTests` remains the owner of the real-server behaviour: the finite schema
matrix, the read-only inspection, the one explicit migration, and cancellation before a statement is
executed. It requires `RUN_SERVICEMANTLE_POSTGRES_TESTS=true` and Docker.

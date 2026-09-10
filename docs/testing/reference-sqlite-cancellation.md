# Reference service SQLite executor cancellation

`ReferenceSqliteMigrationExecutor` is the reference sample's consumer-owned migration boundary for
its SQLite workspace database. This document describes one property of it: when a caller's
cancellation is observed, and what the executor promises - and refuses to promise - about it.

Nothing here changes the startup gate, the deployment declaration, or the file preparation the
caller still owns, and a database this executor calls compatible is still not a completed
installation.

## The finalisation checkpoint

Both public entry points converge on the same rule:

1. The call reads the caller's token before it starts. A cancellation already requested at that point
   means no connection is opened and no migration is started - nothing is read and nothing is
   written.
2. The call does its own work - the read-only observation, or the one `MigrateAsync` on the
   consumer's context.
3. The resources this call owns are released. For the observation that is its own read-only
   `SqliteConnection` and reader.
4. **Only then** is the caller's token read one last time. If cancellation has been requested by that
   checkpoint, the call throws an `OperationCanceledException` whose `CancellationToken` is the
   caller's own token.

The checkpoint outranks whatever the call had already computed. A finite observation decided before
the connection was released is not returned, and a migration that ran to normal completion is not
reported as a success.

## Failures, and cancellations that are not the caller's

| Situation | Result |
| --- | --- |
| Observation fails, caller has not cancelled | `MigrationObservationState.InspectionFailed` |
| Observation fails, caller has cancelled | `OperationCanceledException` with the caller's token |
| Observation throws an `OperationCanceledException` for some other token, caller has not cancelled | `InspectionFailed` |
| Observation completes with any finite state, caller has cancelled | `OperationCanceledException` with the caller's token |
| Execution fails | the migration's own exception, unchanged |
| Execution throws an `OperationCanceledException` for some other token | that exception, unchanged - it is not re-labelled as the caller's |
| Execution completes normally, caller has cancelled | `OperationCanceledException` with the caller's token |

This executor deliberately does **not** classify migration failures. It has no fixed failure type of
its own, and this change adds none: an execution that fails still surfaces the exception the
migration produced. The claim "every provider exception is sanitised" is not made here.

## What this does not guarantee

- **The window after the checkpoint.** A cancellation requested while the result is already
  travelling back to the caller is not caught.
- **Interrupting uncooperative I/O.** SQLite work is largely synchronous. Cancellation is passed
  down and observed at the checkpoint; it does not abort a call already inside the provider, and it
  puts no upper bound on how long a call takes to return.
- **Undoing DDL.** A cancelled execution is not a rolled-back one, and nothing here recovers a
  process killed mid-migration.
- **Anything across processes.** The executor takes no lock and holds nothing between calls. The
  caller still owns file preparation, the deployment declaration, the context, and every transaction.

## How it is covered

`ReferenceSqliteCancellationCompletionTests` covers the checkpoint deterministically:

- The **execution** boundary is driven through EF Core's own public extensibility. The test builds
  the context over an explicit internal service provider and puts its own `IMigrator` in it, so
  `MigrateAsync` can complete normally, fail, or cancel the caller's source at a chosen moment. The
  context still uses the sample's own `ReadWrite` connection string, so no case can create the file.
- The **observation** boundary is driven through a controlled observation. The executor has a
  constructor overload that takes the read-only observation as an operation:

  ```csharp
  var executor = new ReferenceSqliteMigrationExecutor(context, async _ =>
  {
      await using var release = new CancelOnRelease(callerSource);
      return MigrationObservationState.CurrentVersionCompatible;
  });
  ```

  The finite result is decided first and the release runs after it, which is the ordering the
  file-backed observation has when it closes its reader and connection before returning. That
  overload exists for this boundary only. It does not change what the file-backed observation reads,
  the options-based constructors remain the production entry point, and no library internals, no
  `InternalsVisibleTo`, and no cross-provider SPI were added for it.

The same file also keeps one case on the real file-backed path - an absent target is refused and
nothing is created - so the seam cannot quietly become the only thing under test.
`ReferenceSqliteMigrationTests` and `ReferenceSqliteStartupTests` remain the owners of the finite
history matrix, the startup gate, and the deployment contract.

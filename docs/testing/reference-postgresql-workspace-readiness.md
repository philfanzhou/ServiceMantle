# Reference service PostgreSQL workspace readiness

`ReferencePostgreSqlWorkspaceReadinessContributor` is a consumer-owned example of a *business*
readiness veto on PostgreSQL. Given a base-ready snapshot, it accepts only while at least one row can
be observed in `public.reference_workspaces`.

## What this slice delivers, and what it does not

Delivered here: one `IServiceReadinessContributor` with `Order = 100`, over an
`IDbContextFactory<ReferencePostgreSqlDbContext>`.

**Not delivered here:** the real installation-phase snapshot source, the live/ready/compatibility
health endpoints, host activation, and registration. The running sample is unchanged - its old
placeholder still answers `reference.health_not_integrated`, and nothing here makes the sample
`Ready`. The snapshots this contributor is given in tests are its input matrix, not evidence that any
source produces them.

## The decision

| Input | Result |
| --- | --- |
| Snapshot is not base-ready (any phase, migration, or database state the base matrix refuses) | `reference.phase_not_ready` - **no context is created and no SQL runs** |
| Base-ready, and at least one workspace row is observed | ready |
| Base-ready, and the table is empty | `reference.workspace_missing` |
| Base-ready, but creating the context, reading, or releasing it fails - a missing table, a refused read, an unreachable server | `reference.workspace_probe_failed` |
| Base-ready, and an `OperationCanceledException` the caller did not ask for is raised | `reference.workspace_probe_failed` |
| The caller has cancelled by the final checkpoint | `OperationCanceledException` carrying the caller's own token |

The read is `AsNoTracking().AnyAsync()`: existence only. No workspace id, display name, row count, or
provider text reaches the result.

## Ownership and lifetime

Each evaluation creates its own context from the factory and releases it before the evaluation ends.
The contributor caches nothing between evaluations and never captures a caller-scoped `DbContext`, so
it is safe to register as a singleton later.

```csharp
var factory = /* an IDbContextFactory<ReferencePostgreSqlDbContext> the caller owns */;
var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

var result = await contributor.EvaluateAsync(snapshot, cancellationToken);
if (!result.IsReady)
{
    return result.ErrorCode; // one of the fixed codes above
}
```

The caller's token is read once more after the owned context is released, so a cancellation requested
by that point outranks the observation this evaluation had already made.

## What this does not guarantee

- **Ready is a moment, not a state.** It does not prove the snapshot is authoritative, that an
  installation is `Completed`, that any row's fields are valid, or that the schema, configuration,
  audit, capacity, or signing keys are in order. Nothing stops the row from being deleted right after
  it was observed, and no propagation delay is bounded.
- **No timeout of its own.** This contributor builds none. The combiner's shared total budget owns
  the `health.contributor_timeout` classification.
- **No forcible interruption.** Cancellation is passed to the provider and observed at the final
  checkpoint. An uncooperative provider is not aborted, and a cancellation arriving after that
  checkpoint is not covered.
- **One context per evaluation.** A `DbContext` does not support concurrent operations; that is why
  each evaluation owns its own.
- **Logs the caller enables.** The negative assertions cover this contributor's own result and its
  `ToString()`. They say nothing about raw EF Core or Npgsql logging the caller turned on.

## How it is covered

`ReferencePostgreSqlWorkspaceReadinessTests` runs against a real PostgreSQL server and prepares the
schema with EF's own `MigrateAsync`, so it depends on neither the sample's migration executor nor its
setup staging adapters. It asserts that every non-base-ready row is answered without creating a
context or issuing a statement, that an empty table is refused and one or more rows accepted, that a
missing table, a role granted nothing, and an unreachable server all produce the same fixed refusal,
that history, schema and rows are unchanged and no write statement is issued, that a cancellation at
context creation, during the query, at release, and at normal completion all keep the caller's token
while a pre-cancelled evaluation creates nothing, and that each evaluation creates and releases its
own context.

The last three cases compose the contributor with the real `ServiceReadinessContributorCombiner`: its
success and refusal are reported unchanged, a controlled unfinished probe is classified by the shared
budget as `health.contributor_timeout`, and a caller cancellation is reported with the caller's own
token. The fixture releases and awaits the probe it blocked; the test's own wait is not a production
SLA.

It follows the existing real-database policy: `RUN_SERVICEMANTLE_POSTGRES_TESTS=true` with Docker
running. When that environment is explicitly required and unavailable, the tests fail rather than
skip.

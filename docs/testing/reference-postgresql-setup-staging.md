# Reference service PostgreSQL setup staging

The reference sample owns a small example of the *business* half of service setup on PostgreSQL:

- `ReferencePostgreSqlSetupContributor` - an `IServiceSetupContributor` with `Order = 100` that
  validates read-only and stages one workspace row.
- `ReferencePostgreSqlSetupStagingScope` - an `IServiceSetupStagingScope` over the same context.

Both take the caller's own `ReferencePostgreSqlDbContext`. Neither saves, opens a transaction,
commits, rolls back, or disposes anything.

## What this slice delivers, and what it does not

Delivered here: a contributor and a staging scope that can be composed with the real
`ServiceSetupOrchestrator` against a real PostgreSQL database.

**Not delivered here, and not implied by anything below:** host enablement, a Setup Code,
installation state, initial configuration, audit, HTTP, a new table or migration, any DI auto-wiring,
and any lock. Staging a workspace is not an installation that is `Completed`. The sample's host does
not run this contributor during startup, and running it does not make the running sample `Ready`.

## What each member does

| Member | Behaviour |
| --- | --- |
| `ValidateAsync` | Observes the caller's cancellation. Changes no tracked entity and runs no SQL. |
| `RegisterAsync` | Stages exactly one `ReferenceWorkspace` with a fresh `Guid` and the sample's existing default display name. It saves nothing. |
| `HasPendingChanges` | Runs `DetectChanges` and reports whether the tracker holds an `Added`, `Modified`, or `Deleted` entry. |
| `DiscardPendingChangesAsync` | Clears the change tracker. Nothing else. |

Composed with the real orchestrator, the existing protocol holds: a dirty context is refused on entry
with `installation.dirty_context` and its pending work is left alone, every validation runs before
any registration, and a later contributor's rejection, exception, or internal cancellation clears the
uncommitted staging and returns a safe code (`setup.contributor_failed`, or the contributor's own
rejection code).

## Calling it directly

The caller owns the unit of work end to end:

```csharp
await using var context = new ReferencePostgreSqlDbContext(
    new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
        .UseNpgsql(connectionString)
        .Options);

var orchestrator = new ServiceSetupOrchestrator(
    [new ReferencePostgreSqlSetupContributor(context)],
    new ReferencePostgreSqlSetupStagingScope(context));

await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
var result = await orchestrator.OrchestrateAsync(cancellationToken);
if (!result.Succeeded)
{
    await transaction.RollbackAsync(cancellationToken);
    return result.ErrorCode;
}

await context.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
return null;
```

Nothing is visible to another connection until that commit, and a rollback leaves no trace.

## What this does not guarantee

- **No idempotence.** Two explicit registrations stage two rows with two different ids. There is no
  duplicate-install protection, no "already installed" check, and no single winner across instances.
  Setup Codes, completed installation state, and transaction serialization belong to the full
  first-install work, not here.
- **One context, one call.** A `DbContext` does not support concurrent use. The caller must supply a
  dedicated, clean scope per call, and must discard the whole scope whenever it cannot establish that
  the context is clean.
- **Cleanup is tracker-only.** Discarding pending changes clears staged work. It cannot roll back a
  database operation the caller already committed.
- **No external recovery promise.** Nothing here bounds time or recovers from a failure outside the
  database.
- **No secret input.** There is no product input and no secret to leak. The negative assertions cover
  this adapter's and the core orchestration's own results; they say nothing about raw EF Core or
  Npgsql logging the caller enables.

## How it is covered

`ReferencePostgreSqlSetupStagingTests` runs against a real PostgreSQL server and prepares the schema
by calling EF's own `MigrateAsync` on the merged context, so it depends on neither the sample's
migration executor nor any other adapter. It asserts that validation issues no statement and leaves
the tracker untouched, that registration stages exactly one `Added` workspace while an independent
connection still sees nothing, that only the caller's own commit publishes the row and a rollback
leaves none, that the orchestrator's rejection, failure, cancellation, and dirty-context paths behave
as tabled above, that two independent contexts do not affect each other, and that a repeated explicit
registration stages two different ids.

It follows the existing real-database policy: `RUN_SERVICEMANTLE_POSTGRES_TESTS=true` with Docker
running. When that environment is explicitly required and unavailable, the tests fail rather than
skip.

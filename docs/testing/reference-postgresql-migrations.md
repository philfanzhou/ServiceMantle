# Reference service PostgreSQL migrations

The reference sample owns two database contexts. `ReferenceDbContext` is the SQLite context that the
sample's host actually starts, and its migration is written against SQLite's own storage types.
`ReferencePostgreSqlDbContext` is a separate consumer-owned context for PostgreSQL, with its own
migration history and its own storage types.

They are separate on purpose. The SQLite migration declares `TEXT` columns, so pointing the same
context at `UseNpgsql` would not produce a PostgreSQL schema - it would produce a SQLite schema
spelled in PostgreSQL syntax, or fail outright. A PostgreSQL target therefore gets its own context,
its own migration, and its own model snapshot.

## What this slice delivers, and what it does not

Delivered here:

- `ReferencePostgreSqlDbContext`, mapping only the shared `ReferenceWorkspace` business entity to
  `public.reference_workspaces` with `uuid` and `character varying(120)` storage.
- One migration, `20260910000000_InitialReferencePostgreSqlWorkspace`, and its model snapshot.
- `ReferencePostgreSqlMigrationExecutor`, an `IDatabaseMigrationExecutor` whose observation is
  read-only and whose execution is schema-only.

**Not delivered here, and not implied by anything in this document:** the sample's host does not
start on PostgreSQL. There is no registration, no startup coordinator, no target preparation, no
authorization gate, and no installation state. Nothing in this slice initialises an installation,
completes Setup, or writes a row. A schema this executor reports as compatible is a schema
observation and nothing more - it is never evidence that a service installation is `Completed` or
`Ready`. Host startup, target-preparation authorization, installation state initialisation, and
failure recovery remain open work.

Because there is no host wiring, everything below is exercised by calling the executor directly
through its public API.

## Ownership

The caller owns the target database, the connection strings, the context, every save, and every
transaction. The caller also owns serialization of migrations across processes: this executor
registers no lock, acquires none, and holds nothing between calls.

`InspectAsync` opens its own connection from the inspection connection string, reads, and closes.
`ExecuteAsync` runs exactly one explicit `MigrateAsync` on the context it was given. It does not call
`EnsureCreated`, does not call `SaveChanges`, and touches no table beyond the ones its own migration
declares.

The intended call order is: the caller makes sure the target database exists, takes whatever lock its
deployment topology requires, inspects, and only then decides whether to execute. Inspecting first is
not optional - `ExecuteAsync` applies the migration unconditionally and asks no questions.

## The finite schema matrix

The observation covers the `public` schema of the target database and nothing else. It is a finite
set of readable facts, not a general schema-compatibility algorithm.

Two exclusions shape the relation census:

- **System schemas** are not application state and are skipped: `pg_catalog`, `information_schema`,
  and the `pg_toast*` and `pg_temp*` families.
- **The EF history table** `__EFMigrationsHistory` is skipped when counting application relations,
  because it is the executor's own evidence rather than a table the application owns.

| Observation | Result |
| --- | --- |
| `public` exists and is readable, no application relation, and no history table or an empty one | `Empty` |
| History holds a strict, gapless prefix of the ids this build knows | `PendingMigration` |
| History holds exactly the known set, `reference_workspaces` is an ordinary table, and `Id` and `DisplayName` are readable | `CurrentVersionCompatible` |
| History holds an id this build does not know | `VersionTooNew` |
| History is present but the applied ids are not a prefix of the known set (a gap) | `InspectionFailed` |
| History cannot be read at all - missing permission, unexpected shape, unreachable server, missing database, missing `public` schema | `InspectionFailed` |
| Application relations exist but there is no history table | `InspectionFailed` |
| `reference_workspaces` or one of its expected columns is missing | `InspectionFailed` |
| A relation in `public` is not an ordinary table - a view, a materialized view, a partitioned or foreign table | `InspectionFailed` |
| An application relation exists in any non-system schema other than `public` | `InspectionFailed` |

`VersionTooNew` is decided on the unknown history record itself. It is deliberately **not** decided
by asking whether `GetPendingMigrations()` is empty, and an empty workspace table is never taken as
permission to adopt an unknown schema.

The migration set the executor compares against can be supplied explicitly through the three-argument
constructor. That exists so the prefix and gap rows above can be covered without adding a second
production migration the sample has no business need for.

## What is not guaranteed

- The matrix above is the guarantee. Compatibility of an arbitrary physical schema is not: type
  facets, constraints, indexes, triggers, stored procedures, and a history table an administrator has
  hand-edited to look right are all outside it.
- The inspection holds no lock across calls and cannot prevent external DDL between the moment it
  reads and the moment the caller executes.
- Cancellation cannot roll back DDL the server has already committed. Recovery from an arbitrary
  external PostgreSQL failure is not promised, and no command is given an absolute time bound.
- `ExecuteAsync` failures surface as `ReferencePostgreSqlMigrationFailedException` with a fixed
  message, no inner exception, and no provider text. That negative assertion covers the diagnostics,
  results, and exceptions this executor produces itself, including their `ToString()`. It does not
  cover raw EF or Npgsql logging the caller has enabled, the caller's own configuration, or process
  memory.
- Caller cancellation surfaces as `OperationCanceledException` carrying the caller's own token. An
  ordinary failure is never reported as a cancellation.

## Running the tests

`ReferencePostgreSqlMigrationTests` is a real-database test class. It carries the shared
`[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]` classification and uses the shared
required-environment policy, so it is opt-in and, once opted in, cannot silently pass by skipping:
if `RUN_SERVICEMANTLE_POSTGRES_TESTS=true` is set and Docker or the container is unavailable, the
tests fail.

```bash
dotnet build ServiceMantle.slnx -c Release
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test \
  --project tests/ServiceMantle.ReferenceService.Tests -c Release --no-build --no-restore
```

`SERVICEMANTLE_POSTGRES_IMAGE` overrides the container image, which defaults to `postgres:15-alpine`.
Each case creates its own database on the container so no case inherits another's schema.

The rest of `tests/ServiceMantle.ReferenceService.Tests` needs no container and is unaffected: the
sample's default behaviour is still no database side effects at all, and the SQLite deployment
acceptance still starts the sample's real entry point on SQLite.

The container credentials in the fixture are synthetic and exist only for the life of the container.
The tests assert that they do not appear in the executor's own diagnostics, and they never print a
whole connection string, a raw SQL error, or the container's own log.

`eng/packages.json` registers this test project as a real-database project with
`RUN_SERVICEMANTLE_POSTGRES_TESTS=true`, so the release pipeline enables it rather than skipping it
silently.

## Related

- [`MIGRATION_ORCHESTRATION.md`](../../MIGRATION_ORCHESTRATION.md) - the orchestration contract this
  executor plugs into.
- [`reference-sqlite-deployment.md`](reference-sqlite-deployment.md) - the SQLite deployment the
  sample's host actually starts.

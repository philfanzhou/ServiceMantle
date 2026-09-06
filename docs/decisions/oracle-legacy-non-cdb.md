# Oracle legacy non-CDB support remains closed

- Date: 2026-09-06; status: decision fixed, pending PR merge.
- Decision issue: [#205](https://github.com/philfanzhou/ServiceMantle/issues/205).
- Code baseline: `fdac1d591488d57d639a05fc2b8a04faf536ae82`.
- This decision supplements [ADR 0001](0001-oracle-provider-contract.md). It does not
  change support for a local application user in a self-managed, single-instance PDB.

## Decision

ServiceMantle does not support a legacy non-CDB as a Bootstrap target, a target to
observe or prepare, or a target on which to acquire a migration lock. This document's
legacy case is Oracle Database 19c non-CDB. Releases before 19c remain outside the
provider's minimum-version contract, and non-CDB architecture is desupported by Oracle
from Database 21c onward.

| Declared/actual deployment | ServiceMantle decision |
| --- | --- |
| Actual Oracle 19c non-CDB, declared as 19c or later | Closed; runtime topology must reject it. A higher declared version does not convert the database into a PDB. |
| Actual Oracle 19c or later ordinary PDB satisfying ADR 0001 | Existing support retained. |
| Declared server version below 19c | Rejected before connection under the existing minimum-version contract; this decision does not reopen it. |
| Missing or syntactically invalid declared version | Invalid configuration, not evidence about the server's real version or architecture. |
| Claimed 21c-or-later non-CDB | Still closed. Oracle's desupport makes it no more acceptable to ServiceMantle, and the runtime probe remains authoritative if a connection is nevertheless presented. |

Oracle's 19c
[`non-CDB upgrade scenarios`](https://docs.oracle.com/en/database/oracle/oracle-database/19/upgrd/upgrade-scenarios-non-cdb-oracle-databases.html)
state both that non-CDB was deprecated starting in 12.1 and that multitenant is the only
supported architecture in 21c and later. The 19c upgrade guide separately calls 19c the
[`terminal non-CDB upgrade release`](https://docs.oracle.com/en/database/oracle/oracle-database/19/upgrd/overview-conveting-databases-during-upgrade.html).
This is more precise than saying merely that a non-CDB cannot be newly created “after
19c”: Oracle 19c remains the legacy case under discussion; beginning with 21c, creating
or upgrading to non-CDB architecture is desupported.

The support closure is a ServiceMantle contract and evidence decision. It does not claim
that Oracle 19c non-CDB lacks users, schemas, `CREATE USER`, `CREATE SESSION`, or named
locks. Oracle 19c documents ordinary
[`CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/CREATE-USER.html)
and [`DBMS_LOCK`](https://docs.oracle.com/en/database/oracle/oracle-database/19/arpls/DBMS_LOCK.html)
capabilities. ServiceMantle has not validated its complete preparation, compensation,
concurrency, cancellation, and lease-loss contract on that architecture.

## Runtime evidence and identity difference

Every Oracle target, administrative, and migration-lock session opened by the current
provider runs the same topology query. It reads `SESSION_USER`, `CDB_NAME`, `CON_ID`, the
application-container flags, and the cloud-service marker. A supported session must have
a non-empty `CDB_NAME`, a decimal `CON_ID` greater than `2`, both application flags equal
to `NO`, and an empty cloud marker before the non-RAC probe is attempted.

For the legacy shape, the decisive distinction is not user-name syntax. In a non-CDB,
there is no current PDB identity: `CDB_NAME` is empty and container metadata uses the
non-CDB identity rather than a user PDB. Oracle's 19c reference explains that a `CON_ID`
column has value `0` in a
[`non-CDB`](https://docs.oracle.com/en/database/oracle/oracle-database/19/refrn/cdb_-views.html).
Either empty `CDB_NAME`, a non-numeric/empty value, or `CON_ID <= 2` produces
`UnsupportedTopology`. Empty or forged probe fields never produce support.

`SESSION_USER` matching still proves only the configured session identity. It does not
replace the required database/container evidence. Likewise, an `ALL_USERS` row can
establish that a database user exists but cannot turn the session into a PDB target.
Preparation rejects the administrative session before its `ALL_USERS` query, and locking
rejects the target session before any `DBMS_LOCK` allocation.

The declared `ServerVersion` is configuration used for the minimum-version gate. The
provider does not accept it as server-attested topology or as proof that the remote
database is a PDB. A transport or authentication failure before a session exists reveals
neither actual version nor CDB/non-CDB architecture.

## Current behavior at each entry point

The following results describe a syntactically valid direct-password target. Caller
cancellation keeps the existing `OperationCanceledException` behavior and cannot turn an
unsupported topology into success.

| Evidence or stage | Bootstrap Validate | Observe | Prepare | Acquire migration lock |
| --- | --- | --- | --- | --- |
| Declared version below 19c | `database.server_version_unsupported` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| Declared version missing or invalid | `database.server_version_invalid` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_failed` |
| Valid 19c+ declaration; authentication fails before session creation | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`; invalid credentials leave existence unknown, locked/expired accounts set `TargetExists=true` | Administrative login: `database_target_preparation.authentication_failed` | `migration.lock_failed` |
| Valid 19c+ declaration; listener, service, transport, or protocol fails | `database.connection_failed` | `ServerUnreachable(ConnectionFailed)` | Administrative login: `database_target_preparation.connection_failed` | `migration.lock_failed`, or `migration.lock_timeout` when the acquisition deadline wins |
| Connected session has unexpected `SESSION_USER` | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | Administrative session: `database_target_preparation.invalid_target` | `migration.lock_failed` |
| Connected 19c non-CDB returns empty `CDB_NAME` or non-PDB `CON_ID` | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | `database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| Required topology query is denied | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` | `database_target_preparation.permission_denied` | `migration.lock_not_supported` |
| Unexpected topology/probe failure | `database.provider_validation_failed` | `ServerUnreachable(PreparationFailed)` | `database_target_preparation.preparation_failed` | `migration.lock_failed` |

Validate and Observe never perform DDL or lock allocation. Prepare first validates the
target and administrative inputs, opens the administrative session, and rejects the
non-CDB topology before `ALL_USERS`, `CREATE USER`, `GRANT CREATE SESSION`, or any
compensating `DROP USER`. It therefore cannot return `AlreadyExists` or `Created` for a
non-CDB that it actually identifies.

Acquire opens a dedicated unpooled, unenlisted target-user session, then rejects the
non-CDB topology before reading the session ID and before
`DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` or `DBMS_LOCK.REQUEST`. A non-CDB's possible
support for those Oracle APIs is intentionally not used as a fallback. No lease is
returned, so migration execution does not begin.

The `TargetExists=true` value from Observe means only that authentication established a
target session before topology rejection. It does not mean that the non-CDB is supported,
prepared, or a PDB. Conversely, invalid credentials do not prove a missing database user,
and a listener failure does not prove a missing database or identify its architecture.

## Ownership and non-goals

ServiceMantle does not convert or upgrade a non-CDB, create a CDB/PDB, move schemas,
invoke AutoUpgrade or `noncdb_to_pdb.sql`, change `COMPATIBLE`, perform DBA lifecycle
work, or modify its minimum supported version. The consumer and DBA own any database
conversion, backup, outage, rollback, object compatibility, and post-conversion
validation. A database converted to a PDB is a new deployment assertion and must satisfy
the ordinary ADR 0001 runtime and permission contract on its own.

Existing target credentials, schema objects, password/lock state, quota, privileges,
migrations, and transactions remain consumer-owned. ServiceMantle neither mutates a
recognized non-CDB nor promises rollback of side effects created outside the library.
The local/non-Oracle-maintained target-session verification gap tracked by
[#317](https://github.com/philfanzhou/ServiceMantle/issues/317) remains separate; it does
not weaken the non-CDB rejection because the container checks occur independently.

## Evidence required to reopen support

Reopening requires a new decision and implementation task. It must supply a dedicated,
supported Oracle 19c non-CDB environment; Oracle Free's `FREEPDB1` is a PDB and cannot
stand in for it. If the environment, credentials, permissions, or discovered tests are
missing, required CI and release verification must fail rather than skip.

The real test gate must at minimum:

1. Assert the actual server version and non-CDB evidence, including empty/non-CDB
   `CDB_NAME` and `CON_ID=0`, then distinguish it from a separately tested 19c+ ordinary
   PDB. A caller-declared version or mocked probe is not sufficient.
2. Use direct-password target and administrative users with documented minimum grants.
   Verify target identity/schema ownership, missing-user creation, exact existing user,
   conflicting credentials, privilege denial, caller cancellation, timeout, deterministic
   concurrent creation, lost DDL acknowledgement, and compensation ownership. Cleanup
   may remove only identities proved to belong to the run.
3. Grant `EXECUTE` on `SYS.DBMS_LOCK` directly to a dedicated target and run at least two
   real sessions. Prove same-service exclusion, different-service independence, bounded
   timeout, caller cancellation, explicit release/reacquisition, and permanent lease loss
   after terminating the holding session at every migration stage.
4. Run the full Bootstrap/Observe/Prepare/Acquire error matrix without treating missing
   non-CDB infrastructure as a pass. Secrets must remain out of repository content,
   diagnostics, caches, and artifacts.

Until all of that evidence is a required gate, support remains closed. No implementation
task is created by this decision. #317 is the only known adjacent issue and is not fixed
here; no other adjacent defect was found. This document adds no provider code, SQL,
tests, package metadata, CI, README change, database conversion, or pre-19c guarantee.

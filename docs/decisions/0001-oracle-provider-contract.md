# ADR 0001: Oracle target, ownership, lock, and real-CI contract

- Status: Accepted
- Date: 2026-08-30
- Decision issue: [#65](https://github.com/philfanzhou/ServiceMantle/issues/65)
- Implementation issues: [#66](https://github.com/philfanzhou/ServiceMantle/issues/66), [#67](https://github.com/philfanzhou/ServiceMantle/issues/67)

## Context

ServiceMantle already models Oracle as a `ServerSchema` target, but Oracle does not
have a separately creatable schema object: each database user owns exactly one schema
with the same name. Oracle also provides several deployment, identity, and locking
models whose guarantees differ materially. Leaving those choices to the provider
implementation would make target existence, permissions, lock loss, and CI enforcement
conditional rather than contractual.

This decision fixes one supported shape and fails closed for every other shape. It does
not add product code or change the target-preparation or migration-lock SPIs.

## Decision

### 1. Supported Oracle shape

The first Oracle provider supports all of the following, and only the following:

- Oracle Database 19c or later, using the multitenant architecture.
- A self-managed, single database instance reached through one PDB service.
- A local PDB user authenticated directly with `User Id` and `Password` in both the
  target and administrative ODP.NET connection strings.
- Neither connection string enables `DBA Privilege`, integrated/external
  authentication, proxy identity, wallet, token, or operating-system authentication.
- An unquoted user name that is 1-128 ASCII characters, starts with an ASCII letter,
  then contains only ASCII letters, digits, `_`, `$`, or `#`. It is canonicalized to
  uppercase and must not start with `C##`.
- A target password that is 1-30 printable ASCII characters and does not contain a
  double quotation mark. ServiceMantle encloses it in double quotation marks in the
  `CREATE USER` statement. Passwords outside that deliberately narrow, safely
  representable first-provider contract are rejected before administrative DDL.
- The exact same non-empty `Data Source` value in the target and administrative
  connection strings. Equality is ordinal after trimming; ServiceMantle does not infer
  that two TNS aliases or connect descriptors reach the same PDB.
- Every opened target or administrative session must prove these runtime facts:
  `SYS_CONTEXT('USERENV', 'CDB_NAME')` is non-empty; the current `CON_ID` is greater
  than `2`; both `IS_APPLICATION_ROOT` and `IS_APPLICATION_PDB` are `NO`;
  `CLOUD_SERVICE` is empty; and `DBMS_UTILITY.IS_CLUSTER_DATABASE` is `FALSE`.

The target identity is the canonical target `User Id`. The target exists exactly when
the current PDB's `ALL_USERS` view has that `USERNAME` with `COMMON = 'NO'` and
`ORACLE_MAINTAINED = 'N'`. Its owner is that user, because the user and schema are the
same Oracle identity. An empty schema still exists; object count, default tablespace,
quota, roles, and migration state do not change that answer.

ServiceMantle owns observation and an explicitly requested creation attempt. The
consumer owns the target credentials, password rotation, account lock state, profile,
tablespaces and quotas, all schema-object privileges, all schema objects, and all
migrations. ServiceMantle never changes an existing user's password, unlocks it,
changes grants, drops it, or recreates it.

Wallet/mTLS, token, external/OS, and proxy authentication are rejected before opening a
connection as `database_target_preparation.invalid_target`; migration locking for those
identities is `migration.lock_not_supported`. A successfully opened session that proves
RAC, Autonomous Database, CDB root, a common or application-common identity, an
application container, or a legacy non-CDB is likewise rejected as `InvalidTarget` by
observation/preparation and as `migration.lock_not_supported` by locking. Observation
uses `TargetUnreachable(InvalidTarget), TargetExists=true` after such a successful
target connection. A credential or transport failure before a session exists retains
the narrower unknown-existence mapping below; the provider does not claim which hidden
topology rejected an unauthenticated connection. If `DBMS_UTILITY.IS_CLUSTER_DATABASE`
cannot be called, the provider cannot prove the supported topology: observation returns
`TargetUnreachable(PermissionDenied), TargetExists=true`, preparation returns
`database_target_preparation.permission_denied`, and locking returns
`migration.lock_not_supported`. The uncovered shapes' decision issues are listed under
[Follow-up decisions](#follow-up-decisions).

Oracle documents that every user owns one same-named schema and distinguishes local
PDB users from common users in its
[multitenant administration guide](https://docs.oracle.com/en/database/oracle/oracle-database/21/multi/introduction-to-the-multitenant-architecture.html).

### 2. Observation and safe preparation

`ObserveAsync` has only the target connection string, so it must not claim that a user
is missing when Oracle deliberately gives the same authentication response for a
missing user and a wrong password. It applies this matrix:

| Evidence | Observation |
| --- | --- |
| Connection succeeds and `SESSION_USER` equals the canonical target user | `TargetConnectable`, `TargetExists=true` |
| `ORA-01045` (the named user lacks `CREATE SESSION`) | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` |
| `ORA-28000` account locked or `ORA-28001` password expired | `TargetUnreachable(AuthenticationFailed)`, `TargetExists=true` |
| `ORA-01017` invalid credential | `TargetUnreachable(AuthenticationFailed)`, `TargetExists=null` |
| Listener, service, transport, or protocol failure | `ServerUnreachable(ConnectionFailed)` |
| Unsupported identity or malformed connection data | `ServerUnreachable(InvalidTarget)` |
| Connected session proves an unsupported runtime topology | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` |
| Connected session cannot call the required topology probe | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` |
| Other Oracle failure | `ServerUnreachable(PreparationFailed)` |

Consequently, Oracle observation does not return `TargetMissing` from an ambiguous
credential rejection. `PrepareAsync` can establish absence because it temporarily has
an administrative connection and queries `ALL_USERS` in that PDB.

Preparation disables administrative connection pooling and ambient-transaction
enlistment. It first validates the supported identity shape and identical `Data Source`,
opens the administrative connection, then probes `ALL_USERS` before any DDL:

1. If the user exists and the target credentials connect as that user, return
   `AlreadyExists` without modifying it.
2. If the user exists but the target credentials do not connect as that user, return
   `database_target_preparation.target_conflict`. Never reset its password or grants.
3. If the user is absent, issue a safely quoted `CREATE USER ... IDENTIFIED BY ...`,
   followed by `GRANT CREATE SESSION` and a fresh target connection probe.
4. If a concurrent `CREATE USER` wins, re-query and re-probe. Return `AlreadyExists`
   only when the supplied target credentials connect as the expected user; otherwise
   return `TargetConflict`. A probe rejection that follows a positive `ALL_USERS` read
   is reported as `TargetConflict`, never as absence: a concurrent creator may still be
   inside its own create/grant window, or may have just compensated. This call does not
   re-create the user, and the caller may retry.
5. Compensation ownership ends at adoption, not at authorship, and eligibility is
   decided from the statements this call has issued rather than from outcomes it may be
   unable to observe. `DROP USER` without `CASCADE` is attempted only when this
   invocation proved the user absent, received a definite success acknowledgement for
   its own `CREATE USER`, and has **not** yet issued the `GRANT CREATE SESSION`
   statement for it. Issuing the grant is the boundary because Oracle commits DDL
   server-side before acknowledging it: once that statement is on the wire, a lost
   acknowledgement is locally indistinguishable from a rejection, while any concurrent
   call holding the same target credentials may already have connected as that user and
   returned `AlreadyExists` under rule 1 or rule 4. An indeterminate DDL outcome is
   therefore never evidence of exclusive ownership. If the `CREATE USER` acknowledgement
   is lost, this call has not proved authorship and does not compensate. If the grant
   statement was issued at all, this call does not compensate, whatever it observed. Any
   failure, overall timeout, or caller cancellation outside that narrow window leaves
   the created user in place and performs no compensation. ServiceMantle must never
   delete a target that another executor has already reported as prepared, and Oracle
   DDL visibility means "this call created it" does not prove "this call still
   exclusively owns it".
6. When compensation is permitted it runs on its own bounded budget, which is not tied
   to the caller's cancellation token, because the most common trigger for compensation
   is that token already being cancelled. A compensation attempt that does not
   verifiably remove the user returns `PreparationFailed`, because the final state is
   not known. When compensation is not permitted, none is attempted and none can fail,
   so the call reports the triggering failure's own code under the precedence below.

The minimum direct administrative privileges are:

- `CREATE USER` in the target PDB;
- `DROP USER` in the target PDB, used only to compensate a user whose creation this call
  definitely completed and for which it has not yet issued `GRANT CREATE SESSION`;
- `CREATE SESSION WITH ADMIN OPTION`, so the administrator can grant only
  `CREATE SESSION` to the new user.

`GRANT ANY PRIVILEGE`, `DBA`, `SYSDBA`, tablespace administration, and schema-object
creation privileges are not required. A managed or restricted environment that cannot
provide the three direct privileges is unsupported for preparation and fails as
`database_target_preparation.permission_denied`; it is never treated as already
prepared. Both the administrator and target also require the normally available ability
to call `DBMS_UTILITY.IS_CLUSTER_DATABASE`; ServiceMantle neither grants nor repairs
that package access, and fails closed with the codes above if it was revoked. Oracle's
[CREATE USER reference](https://docs.oracle.com/en/database/oracle/oracle-database/21/sqlrf/CREATE-USER.html)
states that `CREATE USER` is required and a new user's privilege domain is empty, while
its [GRANT reference](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/GRANT.html)
requires either the granted privilege's `ADMIN OPTION` or the broader
`GRANT ANY PRIVILEGE`.

More than one of these outcomes can be true at the end of a single call. Preparation
resolves them in this fixed precedence, highest first, so that implementers do not have
to choose:

1. Compensation was permitted, attempted, and did not verifiably remove the user:
   `database_target_preparation.preparation_failed`. This outranks caller cancellation,
   because an `OperationCanceledException` would hide a leaked, non-connectable user.
2. Otherwise caller cancellation: throw `OperationCanceledException`. This holds whether
   or not a permitted compensation succeeded, and whether or not the grant boundary had
   already been crossed.
3. Otherwise the caller-provided overall timeout: `database_target_preparation.timeout`.
4. Otherwise the triggering failure's own code from the table below. A failure that
   occurred while compensation was not permitted always resolves here and never produces
   `preparation_failed` on compensation grounds; a lost administrative session after the
   grant statement was issued, for example, is `connection_failed`.

Preparation failure mapping is fixed as follows:

| Failure | Error code |
| --- | --- |
| Provider mismatch | `database_target_preparation.provider_mismatch` |
| Unsupported identity/topology, identifier/password outside the exact supported grammar, malformed connection, or unequal `Data Source` | `database_target_preparation.invalid_target` |
| Administrative credential rejected | `database_target_preparation.authentication_failed` |
| Required direct privilege missing | `database_target_preparation.permission_denied` |
| Existing user is not connectable with the supplied target identity, including a losing create race | `database_target_preparation.target_conflict` |
| Administrative connection or session is lost, including a DDL statement whose acknowledgement never arrives | `database_target_preparation.connection_failed` |
| The caller-provided overall timeout expires, no permitted compensation failed, and the caller did not cancel | `database_target_preparation.timeout` |
| Unexpected Oracle failure, or a permitted compensation that did not verifiably remove this call's new user | `database_target_preparation.preparation_failed` |
| Caller cancellation, and no permitted compensation failed | throw `OperationCanceledException` |

The provider grants no tablespace quota or schema-object privilege. A created target is
ready for a direct session, not for an arbitrary consumer migration. The consumer DBA
must provision the quota and exact DDL privileges required by its migration executor.

### 3. Migration lock

Oracle migration locking uses `SYS.DBMS_LOCK` on a dedicated, unpooled target-user
session. The consumer DBA must directly grant `EXECUTE ON SYS.DBMS_LOCK` to that user;
ServiceMantle target preparation does not grant it.

The lock name is `ServiceMantle.Migration.` followed by the lowercase hexadecimal
SHA-256 digest of the normalized `ServiceId`. The provider calls
`DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` to obtain a same-name handle, then calls
`DBMS_LOCK.REQUEST` with `X_MODE`, the remaining bounded acquisition timeout, and
`release_on_commit => FALSE`. The lease explicitly calls `DBMS_LOCK.RELEASE` and then
closes the session; session termination also releases the lock.

This strategy was selected because Oracle assigns the same named lock to all sessions,
the full digest avoids reducing service identity to the 30-bit caller-assigned lock-ID
range, and the autonomous allocator cannot commit a consumer work unit. Its catalog row
and lower efficiency are accepted because one migration acquires one lock per service,
not hundreds per session. Oracle documents the allocator, reserved names, catalog
expiration, and autonomous transaction in the
[`DBMS_LOCK` reference](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_LOCK.html).

Rejected alternatives:

- A caller-assigned numeric `DBMS_LOCK` ID would require reducing a 128-character
  `ServiceId` to 30 bits. Collisions would create unrelated-service contention and the
  provider could not distinguish it from legitimate contention.
- A lock table or row lock would add ServiceMantle-owned persistence, DDL, cleanup, and
  transaction ownership to the consumer schema. That contradicts the core package and
  shared-`DbContext` boundaries.
- `ALLOCATE_UNIQUE` (non-autonomous) performs an implicit commit. Even on a dedicated
  connection, the autonomous form expresses the no-consumer-commit invariant directly.

Lock acquisition mapping is fixed as follows:

| Failure | Result |
| --- | --- |
| No registered Oracle lock provider, or target user cannot execute `SYS.DBMS_LOCK` | `migration.lock_not_supported` |
| Overall acquisition deadline expires, including `REQUEST` return code `1` | `migration.lock_timeout` |
| `REQUEST` return code `0` | acquired lease |
| Return code `2`, `3`, `4`, or `5`; allocator failure; connection/authentication failure; unexpected Oracle error | `migration.lock_failed` |
| Caller cancellation before timeout | throw `OperationCanceledException` |

The existing SPI can report failures during `AcquireAsync`, but it cannot notify
`DatabaseMigrationOrchestrator` if the dedicated lock session terminates while
`ExecuteAsync` is still running. Oracle then releases the lock automatically, so
continuing silently would violate the multi-instance guarantee. Issue
[#200](https://github.com/philfanzhou/ServiceMantle/issues/200) must add a provider-neutral
lease-loss signal before #67 implements this lock. A detected mid-migration loss maps to
`migration.lock_failed`; work already performed by the consumer executor is not promised
to roll back.

### 4. Real Oracle CI is mandatory

The real test environment uses Oracle's own Oracle Database Free image and Oracle's
[Free Use Terms and Conditions](https://www.oracle.com/downloads/licenses/oracle-free-license.html),
which permit internal development and testing. CI pins both version and manifest digest:

```text
container-registry.oracle.com/database/free:23.26.1.0-lite-amd64@sha256:ef1a38683b3783b80e033be6b8f2cb31299dcba5430514ec96e2e8f4f0307d15
```

The manifest is `linux/amd64`, matching `ubuntu-24.04`. The image provides the fixed
`FREE` CDB and `FREEPDB1` PDB described by Oracle's
[single-instance container documentation](https://github.com/oracle/docker-images/blob/main/OracleDatabase/SingleInstance/README.md).
The repository does not mirror or redistribute the image.

Every pull request and push CI run that tests the registered Oracle package must:

1. Generate and mask a run-local administrative password; no repository secret or
   manual license click is a start condition.
2. Pull the exact image reference. Pull failure fails the job.
3. Start the container with `ORACLE_PWD` and a random host port. Exit, `unhealthy`, or a
   bounded startup deadline fails the job.
4. Require both container health and a real ODP.NET `SELECT 1 FROM DUAL` against
   `FREEPDB1`; Docker health alone is insufficient.
5. Set `RUN_SERVICEMANTLE_ORACLE_TESTS=true` and supply masked target/admin connection
   strings through the real-database test harness from #115. Missing variables,
   connection failure, zero discovered real tests, any skip, or any failed test fails
   the job.
6. Run the registered package through ReleaseTool build, test, pack, and verify. An
   unavailable Oracle environment fails `test`; it never removes Oracle tests from the
   release gate.

Local runs may omit the opt-in variable and skip Oracle container tests. CI and release
verification may not. The image's future availability is not guaranteed: deletion,
license change, or registry outage intentionally breaks the required job until a new ADR
accepts a replacement or explicitly closes Oracle support.

### 5. Required implementation test matrices

Issue #66 must cover:

- valid descriptor metadata (`Oracle`, `ServerSchema`, 19c+), provider registration,
  case normalization, unsupported identifier/authentication rejection, and fail-closed
  runtime probes for RAC, cloud, root, application container, and non-CDB sessions;
- connectable target, `CREATE SESSION` denial, locked/expired account, ambiguous
  `ORA-01017`, wrong service, cancellation, and safe diagnostics;
- missing-user creation, existing-user protection, wrong-owner/credential conflict,
  concurrent same-credential creation, privilege denial at create/grant/drop,
  compensation attempted before the grant statement is issued, compensation refused once
  it has been issued, compensation refused on an indeterminate `CREATE USER`
  acknowledgement, compensation success/failure, failed compensation outranking caller
  cancellation, overall timeout, cancellation precedence, and administrative pool
  isolation;
- an explicit adoption-race test: one actor creates and grants, a second actor adopts the
  user and returns `AlreadyExists`, then the first actor is cancelled or times out during
  its fresh target probe. The user must survive and the second actor's result must remain
  correct;
- a create/grant-window test proving that a concurrent call observing the user before the
  grant returns `TargetConflict` and neither drops nor re-creates it;
- a lost-acknowledgement test: `GRANT CREATE SESSION` commits on the server but its
  acknowledgement never reaches the issuing call. Assert that no `DROP USER` is issued,
  that the failure is reported as `connection_failed`, and that a concurrent adopter's
  `AlreadyExists` target survives;
- real `FREEPDB1` tests for the same success, failure, cancellation, safety, and
  deterministic two-actor race paths, including the adoption race above. #66 is blocked
  by #115 and #189 so it can use the shared hard-fail harness and the standard
  preparation registration seam.

Issue #67 must cover:

- lock-name fixed vectors, same-service exclusion, different-service independence,
  request timeout, caller cancellation, return-code mapping, direct-execute privilege
  absence, unsupported runtime topology, explicit release, and connection cleanup;
- real two-orchestrator execution proving only one executor runs, lock-held recheck,
  release/reacquire, session termination before acquisition, and deterministic session
  termination during each orchestration stage;
- lease-loss behavior from #200 and hard-fail Oracle CI/ReleaseTool integration from
  #115/#114. #67 may not implement a silent no-lock or session-loss fallback.

### 6. CoddLoom remains out of the dependency graph

The Oracle implementation uses `Oracle.ManagedDataAccess.Core` directly. Inspection of
CoddLoom at
[commit `f23f611`](https://github.com/philfanzhou/CoddLoom/tree/f23f611cda9afaa0eef12cf644af75c3e916de9f)
found no Oracle connection-string builder or reusable identifier-quoting API:
`OracleExecutor` only wraps a supplied full connection string, and `OracleBuilder`
concatenates already-supplied table identifiers for ORM SQL. The ServiceMantle provider
still has to use `OracleConnectionStringBuilder`, implement two small private
quoting/validation helpers, classify Oracle errors, manage privileged connection
pooling, and implement `DBMS_LOCK` itself.

Referencing CoddLoom would therefore add an ORM package and the same ODP.NET dependency
without removing provider code. This measured Oracle result confirms, rather than
invalidates, [#179](https://github.com/philfanzhou/ServiceMantle/issues/179): database
capabilities stay in ServiceMantle.

## Consequences

- `ServerSchema` has one unambiguous Oracle meaning: a local PDB user and its same-named
  schema, not a database, tablespace, common user, or arbitrary current schema.
- Preparation is least-privilege and non-destructive for pre-existing users, but it does
  not make arbitrary migrations possible. Consumers retain schema DDL and quota policy.
- Automatic deletion is confined to a user whose creation this call definitely completed
  and which it has not yet attempted to make connectable, so an `AlreadyExists` result
  returned by one executor can never be undone by another executor's later failure,
  timeout, or cancellation.
- A restricted environment without user-management privileges or `DBMS_LOCK` fails with
  a stable existing code; there is no conditional enablement.
- `ALLOCATE_UNIQUE_AUTONOMOUS` writes Oracle's own lock-name catalog row. It writes no
  ServiceMantle table and commits no consumer transaction.
- #67 cannot begin until the provider-neutral lease-loss gap is closed by #200.
- A pinned proprietary-but-free image gives reproducibility and a clear license source,
  while intentionally making registry or license loss a visible build failure.

## Explicit non-guarantees

- No support is promised for the follow-up deployment and authentication shapes below.
- `ObserveAsync` does not distinguish a missing user from a wrong password after
  `ORA-01017`; it reports unknown existence.
- Preparation does not grant quota, object DDL privileges, roles, or `DBMS_LOCK` access,
  and does not repair an existing account.
- Failed compensation can leave a newly created but non-connectable user; the result is
  `PreparationFailed`, it outranks caller cancellation, and it requires consumer DBA
  remediation.
- Preparation is not transactional. A call that fails, times out, or is cancelled after
  it issued its `GRANT CREATE SESSION` deliberately leaves the created user in place; the
  state converges on a later call rather than rolling back.
- Compensation eligibility is deliberately conservative rather than complete. An
  indeterminate `CREATE USER` or `GRANT CREATE SESSION` outcome suppresses compensation
  entirely, so a failed preparation can leave a created but ungranted, non-connectable
  user behind for a later call or the consumer DBA to converge.
- Simultaneous first-time preparers are not promised a single-attempt success. A call
  that observes another call's create/grant window returns `TargetConflict` rather than
  `AlreadyExists`, and the ADR only promises that no such call is destructive and that a
  retry converges.
- Oracle session loss cannot undo migration work already performed before the loss was
  detected.
- CI proves the pinned Oracle Database Free build on `linux/amd64`; it does not prove
  every 19c+ release update, operating system, cloud service, or topology.

## Follow-up decisions

- [#201](https://github.com/philfanzhou/ServiceMantle/issues/201): Oracle RAC and connection failover.
- [#202](https://github.com/philfanzhou/ServiceMantle/issues/202): Autonomous Database.
- [#203](https://github.com/philfanzhou/ServiceMantle/issues/203): CDB root and common users.
- [#204](https://github.com/philfanzhou/ServiceMantle/issues/204): wallet, token, external, and proxy authentication.
- [#205](https://github.com/philfanzhou/ServiceMantle/issues/205): legacy non-CDB deployments.

## References

- [Oracle multitenant user and schema model](https://docs.oracle.com/en/database/oracle/oracle-database/21/multi/introduction-to-the-multitenant-architecture.html)
- [Oracle `CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/21/sqlrf/CREATE-USER.html)
- [Oracle `GRANT`](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/GRANT.html)
- [Oracle password requirements](https://docs.oracle.com/en/database/oracle/oracle-database/19/dbseg/minimum-requirements-passwords.html)
- [Oracle `ALL_USERS`](https://docs.oracle.com/en/database/oracle/oracle-database/21/refrn/ALL_USERS.html)
- [Oracle `DBMS_LOCK`](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_LOCK.html)
- [Oracle `DBMS_UTILITY.IS_CLUSTER_DATABASE`](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_UTILITY.html#GUID-EAFE94FC-5BEA-42C4-B70A-8C18DBE9EC20)
- [Oracle Database Free container documentation](https://github.com/oracle/docker-images/blob/main/OracleDatabase/SingleInstance/README.md)
- [Oracle Free Use Terms and Conditions](https://www.oracle.com/downloads/licenses/oracle-free-license.html)
- [`Oracle.ManagedDataAccess.Core` 23.26.300](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/23.26.300)

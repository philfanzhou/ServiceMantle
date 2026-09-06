# Oracle container target support remains closed

- Date: 2026-09-06; status: decision fixed, pending PR merge.
- Decision issue: [#203](https://github.com/philfanzhou/ServiceMantle/issues/203).
- Code baseline: `fdac1d591488d57d639a05fc2b8a04faf536ae82`.
- This decision supplements [ADR 0001](0001-oracle-provider-contract.md). It does not
  change the existing single-instance, ordinary-PDB contract.

## Decision

ServiceMantle continues to support only a local, non-Oracle-maintained application user
in an ordinary user-created PDB. CDB root, PDB seed, CDB common users, application roots,
application PDBs, and application-common users remain unsupported for Bootstrap target
validation, observation, preparation, and migration locking.

This is a closed support decision, not a promise that the current parser recognizes every
unsupported common identity before connecting. There is no feature switch, container
switch, common grant, cross-container fallback, or unlocked migration path.

| Connected container and target identity | Decision | Current evidence and boundary |
| --- | --- | --- |
| Ordinary user-created PDB; local user with `COMMON=NO` and `ORACLE_MAINTAINED=N` | Existing support retained | `CON_ID > 2`, both application-container flags are `NO`, cloud marker is empty, and the database is non-RAC. Preparation also verifies the target row in `ALL_USERS`. |
| `CDB$ROOT` (`CON_ID=1`) | Closed | The runtime topology probe rejects `CON_ID <= 2`. |
| `PDB$SEED` (`CON_ID=2`) | Closed | The same probe rejects it; ServiceMantle does not attempt to modify the seed. |
| Ordinary PDB; CDB common user | Closed | A default `C##` name is rejected before connection. A customized or empty `COMMON_USER_PREFIX` can evade that name check; the current target-session probe does not query `ALL_USERS`, as tracked by [#317](https://github.com/philfanzhou/ServiceMantle/issues/317). |
| Application root | Closed | `IS_APPLICATION_ROOT=YES` is rejected after connection. |
| Application PDB, whether its target is local or application-common | Closed | `IS_APPLICATION_PDB=YES` is rejected after connection. |
| Application-common user in an application root | Closed | The application-root topology is rejected; the user-name prefix is not accepted as proof of identity. |

Oracle's [`COMMON_USER_PREFIX`](https://docs.oracle.com/en/database/oracle/oracle-database/26/refrn/COMMON_USER_PREFIX.html)
is configurable and has different defaults in the CDB root and an application root.
Therefore, “does not start with `C##`” does not prove that a user is local. Conversely,
a connection alias or user name does not identify a database container.

[`ALL_USERS`](https://docs.oracle.com/en/database/oracle/oracle-database/26/refrn/ALL_USERS.html)
provides user metadata visible to the current session, including `COMMON`,
`ORACLE_MAINTAINED`, and `INHERITED`. Container identity is separate evidence:
[`SYS_CONTEXT`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SYS_CONTEXT.html)
provides the current `CON_ID`, `CON_NAME`, and session user. The supported target requires
both kinds of evidence; neither a naming convention nor a TNS alias substitutes for them.

## Current behavior at each entry point

The tables below describe the implementation at the stated baseline. They deliberately
distinguish a declared support boundary from complete detection. A failure before an
authenticated session exists cannot reveal a hidden container or user type.

### Bootstrap Validate

| Evidence or failure | Current result | Side effects |
| --- | --- | --- |
| Unsupported user-name or authentication shape, including a literal `C##` prefix | `database.connection_string_invalid` | No connection, DDL, or lock allocation. |
| Locked/expired account or invalid credentials before a session is established | `database.authentication_failed` | No container or common-user inference; no DDL or lock allocation. |
| Listener, service, transport, or protocol failure | `database.connection_failed` | No container or common-user inference; no DDL or lock allocation. |
| Target lacks `CREATE SESSION` | `database.permission_denied` | No DDL or lock allocation. |
| Connected session reports root, seed, or an application container | `database.connection_string_invalid` | Probe only; no DDL or lock allocation. |
| Required topology probe is denied | `database.permission_denied` | No DDL or lock allocation. |
| `SESSION_USER` differs from the normalized target user | `database.connection_string_invalid` | No DDL or lock allocation. |
| Unexpected provider/probe failure | `database.provider_validation_failed` | No DDL or lock allocation. |

Validation does not query the connected target user's `ALL_USERS` row. A common user in
an ordinary PDB whose name is not rejected by the literal `C##` check can therefore pass
the present topology probe. That is the existing #317 detection gap, not supported
behavior and not a guarantee added by this decision.

### Observe

| Evidence or failure | Current result | Side effects |
| --- | --- | --- |
| Unsupported user-name or authentication shape | `ServerUnreachable(InvalidTarget)` | No connection, DDL, or lock allocation. |
| Connected root, seed, or application-container session | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | Probe only. |
| Topology-probe permission denial | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` | Probe only. |
| Connected session identity mismatch | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | Probe only. |
| Invalid credentials before a session exists | `TargetUnreachable(AuthenticationFailed)`, existence unknown | No hidden topology or user-type inference. |
| Locked or expired account | `TargetUnreachable(AuthenticationFailed)`, `TargetExists=true` | No DDL or lock allocation. |
| Listener, service, transport, or protocol failure | `ServerUnreachable(ConnectionFailed)` | No DDL or lock allocation. |
| Other Oracle failure | `ServerUnreachable(PreparationFailed)` | No DDL or lock allocation. |

Observation never performs administrative discovery or DDL. It has the same #317 gap
for a successfully connected common target in an ordinary PDB.

### Prepare

Preparation first validates both connection strings and their exact trimmed `Data Source`
match. It then opens an unpooled administrative session and applies the runtime topology
probe before querying or modifying the target.

| Evidence or failure | Current result | Side-effect stopping point |
| --- | --- | --- |
| Unsupported target/admin shape, invalid target name/password, or unequal data sources | `database_target_preparation.invalid_target` | Before the administrative connection and before DDL. |
| Administrative session is in root, seed, or an application container | `database_target_preparation.invalid_target` | Before `ALL_USERS` and before DDL. |
| Administrative topology probe is denied | `database_target_preparation.permission_denied` | Before `ALL_USERS` and before DDL. |
| Administrative `SESSION_USER` differs from its configured user | `database_target_preparation.invalid_target` | Before `ALL_USERS` and before DDL. |
| Administrative credentials are rejected | `database_target_preparation.authentication_failed` | Before `ALL_USERS` and before DDL. |
| Administrative connection or session is lost | `database_target_preparation.connection_failed` | Statements already acknowledged can have occurred; ADR 0001 fixes compensation eligibility. |
| Target row has `COMMON!=NO` or `ORACLE_MAINTAINED!=N` | `database_target_preparation.target_conflict` | `ALL_USERS` was read; no create, grant, or drop is issued. |
| Target local user is absent in the supported ordinary PDB | Existing create path | `CREATE USER`, then `GRANT CREATE SESSION`, subject to ADR 0001 compensation rules. |
| Required create/grant/drop privilege is denied | `database_target_preparation.permission_denied` | Only statements reached before the denial can have occurred. |
| Overall deadline expires | `database_target_preparation.timeout` | ADR 0001 cancellation and compensation precedence applies. |
| Unexpected Oracle failure, or eligible compensation cannot verify removal | `database_target_preparation.preparation_failed` | Previously acknowledged statements are not treated as rolled back. |

The SQL contains no `SET CONTAINER` and no `CONTAINER=ALL`. Oracle's
[`CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/CREATE-USER.html)
rules make `CONTAINER=CURRENT` the local-user meaning in a PDB; the current implementation
relies on that scope by omitting the clause. It likewise issues no common
`GRANT ... CONTAINER=ALL`. The administrative account may itself be common when connected
to an ordinary PDB because the current topology probe does not prove that account local;
this does not authorize cross-container work. Target discovery still rejects a visible
common or Oracle-maintained target row before DDL.

### Acquire migration lock

| Evidence or failure | Current result | Side-effect stopping point |
| --- | --- | --- |
| Unsupported version, authentication, or name shape such as literal `C##` | `migration.lock_not_supported` | Before connection and lock allocation. |
| Malformed provider/configuration or missing data source | `migration.lock_failed` | Before lock allocation. |
| Connected root, seed, or application-container session | `migration.lock_not_supported` | Before `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` and `REQUEST`. |
| Topology-probe permission denial | `migration.lock_not_supported` | Before lock allocation. |
| Connected session identity mismatch | `migration.lock_failed` | Before lock allocation. |
| Authentication, connection, or unexpected Oracle failure | `migration.lock_failed`; acquisition deadline remains `migration.lock_timeout` | No allocation unless all earlier checks succeeded. |
| Target cannot execute the required `SYS.DBMS_LOCK` calls | `migration.lock_not_supported` | Allocation or request can have been attempted, but no valid lease is returned. |

After a successful topology probe, the current lock name is derived from only the
normalized `ServiceId`. The provider does not incorporate a database ID, container ID,
user name, or connection alias. That is sufficient only inside the existing single-target
contract. A connection alias and a user name are specifically not accepted as a future
cross-container lock identity. Because Acquire also has the #317 target-session gap, a
common user with a non-rejected name in an ordinary PDB can reach lock allocation today;
that is not supported or made safe by this document.

Caller cancellation, bounded timeouts, preparation compensation, lock release, and
lease-loss behavior remain exactly as specified by ADR 0001. Cancellation does not
convert an unsupported topology into success, and ServiceMantle does not promise to
undo consumer DDL.

## Why common and cross-container operations are not enabled

Oracle permits common-user creation only from an appropriate root. `CONTAINER=ALL`
changes the scope of creation or grants, while `CONTAINER=CURRENT` limits them to the
current container. Application-common identities have a separate application-root and
synchronization lifecycle. Those operations require a larger privilege and ownership
contract than the current local-PDB provider. ServiceMantle does not silently expand
`CREATE USER`, `DROP USER`, or `CREATE SESSION WITH ADMIN OPTION` into root-level common
administration.

The database/container identity is also part of safe lock scoping. Supporting multiple
containers would require a server-verified, canonical identity such as database identity
plus container identity (`CON_ID` and a stable container identifier or equivalent), not
an unverified alias. The exact identity must survive multiple service names for the same
target while keeping distinct PDBs from aliasing one lock namespace.

## Requirements to reopen support

Reopening any closed row requires a separate decision and implementation task. It must
provide all of the following automated evidence; unavailable infrastructure keeps the
feature closed and must not produce a skipped or falsely green required job.

1. A dedicated real CDB environment containing root, seed, at least two ordinary PDBs,
   and an application root/application PDB. Tests must assert server-reported database,
   container, session-user, `COMMON`, `ORACLE_MAINTAINED`, and `INHERITED` evidence rather
   than infer it from names or aliases.
2. Explicit support rules for CDB common, application-common, and local users under
   customized, default, and empty `COMMON_USER_PREFIX` values. #317 must be resolved for
   every entry point that claims the identity.
3. A least-privilege matrix for `CREATE USER` and `GRANT` with explicit
   `CONTAINER=CURRENT` or `CONTAINER=ALL`, including create races, cancellation, lost DDL
   acknowledgements, compensation ownership, and proof that unrelated containers are
   never modified. No test may use broad DBA/SYSDBA access to hide the required grants.
4. A canonical database-and-container lock identity and two-session tests. Two actors
   targeting the same canonical container and service must contend; actors in distinct
   PDBs must not alias accidentally; release, timeout, cancellation, and killed-session
   lease loss must remain deterministic.
5. Required CI and release gates for each claimed topology. Missing credentials,
   permissions, containers, discovered tests, or assertions must fail rather than skip.

No implementation task is created by this closed decision. #317 remains the known
adjacent defect and is not fixed here; no other adjacent debt was found. This document
adds no provider code, SQL, tests, package metadata, CI, README change, or guarantee for
cross-container behavior.

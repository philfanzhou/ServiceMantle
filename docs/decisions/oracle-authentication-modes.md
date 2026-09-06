# Oracle non-password authentication support remains closed

- Date: 2026-09-06; status: decision fixed, pending PR merge.
- Decision issue: [#204](https://github.com/philfanzhou/ServiceMantle/issues/204).
- Code baseline: `fdac1d591488d57d639a05fc2b8a04faf536ae82`;
  `Oracle.ManagedDataAccess.Core` 23.26.300.
- This decision supplements [ADR 0001](0001-oracle-provider-contract.md) and does not
  extend the Autonomous Database decision in [ADR 0006](0006-oracle-autonomous-database.md)
  to every self-managed deployment.

## Decision

ServiceMantle continues to support only a directly authenticated Oracle database user
whose target connection string contains an ordinary `User Id`, a password, and a non-empty
`Data Source`. The administrative connection used by preparation has the same direct
user/password requirement. The supported runtime identity is that database user's
same-named schema in the same supported ordinary PDB.

The following authentication families remain closed for target validation, observation,
preparation, and migration locking:

| Authentication family | Decision | Identity and credential ownership outside ServiceMantle |
| --- | --- | --- |
| Wallet-backed client authentication and mTLS | Closed | The consumer owns wallet acquisition, files, passwords, certificate trust, renewal, revocation, file permissions, and cleanup. A wallet used only as a TLS trust store does not by itself identify the schema owner. |
| OCI IAM, Microsoft Entra ID/OAuth, or other ODP.NET token authentication | Closed | The consumer owns the workload/user identity, token acquisition, private key, callback, refresh, expiry, audience, files, and pool lifecycle. ServiceMantle accepts no token or refresh callback. |
| External or operating-system authentication, including NTS, Kerberos, and certificate-mapped external users | Closed | The consumer owns the client/process identity, Oracle Net configuration, external-name mapping, platform restrictions, and credential renewal. `User Id=/` is not a supported target identity. |
| Proxy authentication, including two-session attributes and `proxy[client]` syntax | Closed | The consumer owns proxy and client credentials, `CONNECT THROUGH` authorization, role restrictions, audit identity, proxy-session state, and pool isolation. ServiceMantle does not choose which identity owns migrations. |

There is no hidden opt-in, passwordless fallback, automatic credential discovery, proxy
session switch, or weaker lock mode. ODP.NET's ability to open one of these connections
does not establish ServiceMantle support.

## Explicit inputs and configuration ServiceMantle cannot prove

The current target parser explicitly rejects a connection builder that serializes any of
`DBA Privilege`, `Proxy User Id`, `Proxy Password`, `Wallet Location`, or
`Token Authentication`. It also rejects a missing/empty password, `User Id=/`, and user
names outside the narrow unquoted identifier grammar. The bracket characters in ODP.NET's
single-session `proxy[client]` syntax are outside that grammar.

The migration-lock entry point first uses a generic connection-string parser to recognize
the five named attributes even if the pinned ODP.NET builder rejects a keyword. This is a
deliberate `migration.lock_not_supported` precheck. It does not turn every unknown,
malformed, or version-dependent keyword into a supported-authentication diagnosis.

ODP.NET also supports configuration outside those explicit inputs. Its
[`OracleConfiguration` secure connection properties](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConfigurationSecureConnectionProperties.html)
include process-level wallet, token, and Oracle Net settings. `tnsnames.ora`, `sqlnet.ora`,
`TNS_ADMIN`, a system certificate store, database-side identity mapping, or code that
mutates ODP.NET global state can affect a connection without a corresponding serialized
attribute. Oracle documents that managed ODP.NET external authentication methods can be
selected through
[`SQLNET.AUTHENTICATION_SERVICES`](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallManagedConfig.html).

ServiceMantle does not parse, snapshot, isolate, reset, or attest those process-level,
file-level, platform, or server-side settings. Consequently:

- absence of `Wallet Location`, `Token Authentication`, proxy attributes, or `User Id=/`
  is not proof that none of these authentication paths influenced ODP.NET;
- a TCPS connection using the platform certificate store may be ordinary one-way TLS;
  transport success is not a declaration of wallet/mTLS identity support;
- this decision does not weaken ODP.NET host-name, distinguished-name, certificate-chain,
  or TLS-version validation, and ServiceMantle adds no alternative trust path;
- a connection failure before authentication completes cannot reveal the hidden
  authentication method, database identity, container, or schema owner.

The runtime topology query currently verifies `SESSION_USER` against the normalized
configured user plus the existing PDB/cloud/RAC facts. It does not read
`AUTHENTICATION_METHOD`, `AUTHENTICATED_IDENTITY`, `PROXY_USER`, or `CURRENT_SCHEMA`.
Oracle's [`SYS_CONTEXT`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SYS_CONTEXT.html)
defines those as distinct evidence: for example, `PROXY_USER` is the database user that
opened a session on behalf of `SESSION_USER`, while `CURRENT_SCHEMA` can change during a
session. A matching `SESSION_USER` alone therefore does not prove direct password
authentication, absence of a proxy, credential provenance, or unchanged schema context.

This incomplete runtime authentication detection is a declared non-guarantee, not an
expansion of the support surface. The separate local/non-Oracle-maintained target identity
gap remains tracked by [#317](https://github.com/philfanzhou/ServiceMantle/issues/317).

## Current errors and side-effect boundary

All four public entry points give an already-cancelled caller
`OperationCanceledException` with its token before invalid-input classification.
Cancellation observed during I/O is also preserved, except for ADR 0001's existing
Prepare rule where a permitted compensation attempt that cannot verify removal outranks
caller cancellation as `database_target_preparation.preparation_failed`. The table below
lists non-cancellation outcomes at the current baseline.

| Evidence | Bootstrap Validate | Observe | Prepare | Acquire migration lock |
| --- | --- | --- | --- | --- |
| Explicit `Wallet Location`, `Token Authentication`, proxy attribute, or `DBA Privilege` | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_not_supported` through the generic attribute precheck |
| `User Id=/`, empty password, or bracket proxy syntax | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_not_supported` after the ODP.NET builder/target-identity check |
| Unknown attribute, malformed syntax, or invalid attribute value | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_failed`; it is not promised as an unsupported-authentication diagnosis |
| Missing or empty `Data Source` | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`, existence unknown | `database_target_preparation.invalid_target` | `migration.lock_failed` |
| Invalid credentials before a target session exists | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`, existence unknown | Administrative login: `database_target_preparation.authentication_failed`; existing-target probe and post-create probe: `database_target_preparation.target_conflict` | `migration.lock_failed` |
| Locked or expired target account | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`, `TargetExists=true` | Existing-target probe and post-create probe: `database_target_preparation.target_conflict` | `migration.lock_failed` |
| Target lacks `CREATE SESSION` | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` | Administrative login: `database_target_preparation.permission_denied`; existing-target probe: `database_target_preparation.target_conflict`; post-create probe: `database_target_preparation.permission_denied` | `migration.lock_failed` |
| Connected `SESSION_USER` does not match the configured identity | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | Administrative session: `database_target_preparation.invalid_target`; existing-target probe: `database_target_preparation.target_conflict`; post-create probe: `database_target_preparation.invalid_target` | `migration.lock_failed` |
| Connected session proves unsupported topology | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`, `TargetExists=true` | Administrative session: `database_target_preparation.invalid_target`; existing-target probe: `database_target_preparation.target_conflict`; post-create probe: `database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| Required topology probe is denied | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`, `TargetExists=true` | Administrative session: `database_target_preparation.permission_denied`; existing-target probe: `database_target_preparation.target_conflict`; post-create probe: `database_target_preparation.permission_denied` | `migration.lock_not_supported` |
| Listener, service, transport, or protocol failure | `database.connection_failed` | `ServerUnreachable(ConnectionFailed)` | Administrative session: `database_target_preparation.connection_failed`; existing-target probe: `database_target_preparation.target_conflict`; post-create probe: `database_target_preparation.connection_failed` | `migration.lock_failed`, or `migration.lock_timeout` when the acquisition deadline wins |
| Unexpected provider/Oracle failure | `database.provider_validation_failed` | `ServerUnreachable(PreparationFailed)` | `database_target_preparation.preparation_failed` | `migration.lock_failed` |

Input rejection occurs before any provider I/O, administrative DDL, or lock allocation.
Validate and Observe never issue DDL or allocate a lock. Prepare reaches `ALL_USERS` and
then `CREATE USER`/`GRANT CREATE SESSION` only after the administrative input, connection,
identity, and topology checks succeed; ADR 0001's compensation and error precedence remain
unchanged. Acquire opens a dedicated session and completes its identity/topology probe
before `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` and `REQUEST`.

The external-configuration blind spot means an unrecognized authentication mode might
reach those later operations if ODP.NET produces the expected session identity. This is
not a guarantee that all unsupported modes are rejected before side effects. It is why
future support requires explicit credential and identity contracts rather than relying
on connection success.

## Credential, pooling, and transaction ownership

Target and administrative connection strings remain caller-supplied inputs. ServiceMantle
does not persist, export, renew, rotate, download, or log their credentials. It also does
not promise that the caller, process, ODP.NET diagnostics, Oracle Net, or the database
does not retain or log data outside the library's owned result and exception projection.

Bootstrap validation and target observation preserve the target builder's current
pooling/enlistment settings, apart from applying the bounded connection timeout.
Preparation forces only the administrative connection to `Pooling=false` and
`Enlist=false`; target verification retains the target connection's settings. Migration
locking forces its dedicated target-user connection to `Pooling=false` and
`Enlist=false`. The lock's physical session owns the `DBMS_LOCK` lease, while the consumer
continues to own schema objects, migrations, and any consumer transaction. Adding a
wallet, token, external identity, or proxy would require explicit rules for pool keys,
credential expiry, session reset, and lease loss; the present rules do not supply them.

## Evidence required to reopen a mode

Any future support proposal must begin with a separate decision and, when credentials or
target identity cannot fit the existing configuration, a provider-neutral credential or
target SPI task. It must preserve the same-PDB target contract unless a separate container
decision has first reopened that boundary. Missing infrastructure, credentials,
permissions, or discovered tests must fail required CI rather than skip.

Every reopened mode needs a least-privilege target and administrator where applicable,
machine-verified session/schema/proxy/container identity, Bootstrap and preparation
failure/cancellation tests, and two independent lock sessions proving same-service
exclusion, different-service independence, timeout, release, and killed-session lease
loss. Mode-specific evidence is also mandatory:

1. **Wallet/mTLS:** a real self-managed server and client-certificate mapping, fixed TLS
   and trust policy, valid/expired/revoked/wrong-DN certificates, password-protected and
   auto-login wallets, secure temporary materialization, renewal during new connections,
   pool isolation, and deterministic deletion of only run-owned wallet files.
2. **Token:** each claimed OCI IAM or Entra flow, a trusted workload/user identity source,
   audience and database mapping, application-supplied and provider-acquired tokens as
   applicable, refresh-callback races, expired/revoked/wrong-audience tokens, new versus
   already-open pooled sessions, private-key/file cleanup, and no token in diagnostics.
   Oracle's [token connection documentation](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featConnecting.html)
   makes refresh of application-supplied credentials an application lifecycle concern;
   it is not implemented by the current connection-string-only SPI.
3. **External/OS:** each supported platform and method (for example NTS or Kerberos),
   isolated process identities, Oracle Net and database mappings, expected
   `AUTHENTICATION_METHOD`/`AUTHENTICATED_IDENTITY`, unavailable ticket or identity,
   renewal and revocation, and proof that another service process cannot inherit the
   target merely through shared machine configuration.
4. **Proxy:** both claimed ODP.NET proxy forms, explicit proxy/client/schema ownership,
   `SESSION_USER`, `PROXY_USER`, authentication method, direct grants and restricted
   roles, missing/revoked `CONNECT THROUGH`, wrong client, pool reuse/session reset, and
   proof that lock allocation and migration execute under the intended client schema.
   ODP.NET documents the two identities and connection attributes in its
   [connection-string reference](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConnectionConnectionString.html).

Secret material must enter only through protected CI identity or secret facilities,
remain absent from repository content, cache, artifacts, test names, and sanitized
results, and be removed in `finally` from a run-exclusive location. Tests must not grant
DBA/SYSDBA merely to conceal the privilege contract or reuse Oracle Free as evidence for
an authentication service it does not provide.

No implementation task is created by this closed decision. #317 remains known adjacent
debt and is not fixed here; no other adjacent defect was found. This document adds no
provider code, credential SPI, SQL, tests, package metadata, CI, README change, or support
guarantee for the four closed authentication families.

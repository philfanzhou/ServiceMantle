# Management entry authorization decision (#312)

Status: accepted design for issues #94, #95, #96, and #97. This document defines contracts; it does
not implement an endpoint.

## One versioned surface, distinct entry kinds

Every entry is a direct child of the root configured by `AddServiceMantleManagementApiV1`. The
default root is `/management/v1`; examples below use `{v1}` for that configured root. Path versioning
is the only version negotiation mechanism.

These entries do not weaken the protected group returned by `MapServiceMantleManagementApiV1`.
Issue #323 owns a separate, opt-in entry convention and method-aware phase classification. Startup
validation rejects an entry mapped through the protected group, an anonymous override on that group,
the wrong path or method, duplicate entry metadata, weaker rate limiting, missing security headers,
and any authentication rule other than the one fixed below.

| Entry | Method and path | Admitted phase | Authentication or credential | Rate limit |
| --- | --- | --- | --- | --- |
| Installation status | `GET`, `HEAD {v1}/status` | All defined phases and migration/database states | Anonymous | Management policy, anonymous client partition |
| Bootstrap create | `POST {v1}/bootstrap` | `BootstrapConfiguration` only; migration must not be Running or Failed | Anonymous transport plus one local Bootstrap credential | Setup policy, client IP partition |
| Bootstrap update | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` only | Current management cookie with Admin permission | Management policy, operator partition |
| Setup status | `GET`, `HEAD {v1}/setup` | `PendingSetup + Succeeded + Reachable`, or `Completed + Succeeded + Reachable` | Anonymous | Setup policy, client IP partition |
| Setup complete | `POST {v1}/setup` | Same as Setup status so a completed replay reaches a stable conflict | Anonymous transport plus current Setup Code while pending | Setup policy, client IP partition |
| Login | `POST {v1}/session/login` | `Completed + Succeeded + Reachable` only | Anonymous transport; consumer login adapter obtains credentials from its trusted scoped accessor | Setup policy, client IP partition |
| Logout | `POST {v1}/session/logout` | `Completed + Succeeded + Reachable` only | Any valid ServiceMantle management identity cookie | Management policy, operator partition |
| Current session | `GET`, `HEAD {v1}/session` | `Completed + Succeeded + Reachable` only | Any valid ServiceMantle management identity cookie | Management policy, operator partition |

All other methods follow normal routing behavior and never reach a handler. Reserved branch matching
uses the existing segment-aware, case-insensitive Phase Gate rules; a look-alike such as
`/bootstrap-other` is not a Bootstrap entry. The configured root must still end in one independent
`/v1` segment.

The Phase Gate runs before authentication, rate limiting, authorization, and endpoint handlers.
Consequently a wrong phase returns the existing `503 {"errorCode":"service.phase.unavailable"}`
without inspecting a credential or cookie. Except for installation status, each admitted request is
based on exactly one Gate snapshot. A phase change after admission does not recall the request;
file/database concurrency and immutable create/consume operations remain the handler's authority.

## Common HTTP and security boundary

Every entry has the existing correlation middleware and mandatory security-response headers. The
existing Cookie responses remain unchanged:

- no cookie: `401 {"errorCode":"management.session.unauthenticated"}`;
- a presented but unacceptable or expired cookie:
  `401 {"errorCode":"management.session.expired"}`;
- a valid identity without the required permission, or with invalid ServiceMantle claims:
  `403 {"errorCode":"management.session.forbidden"}`.

The existing rate limiter retains its `429 application/problem+json` contract. Gate, authentication,
and rate-limit rejections execute no endpoint adapter, credential check, provider, file write,
database stage, cookie sign-in, or cookie sign-out.

Every unsafe entry requires exactly one `X-ServiceMantle-Request` header with the exact value `1`.
Missing, empty, duplicate, comma-combined, or different values produce the fixed management invalid
request response before the body or credential is processed. JSON-writing entries also accept only
`application/json` with an optional UTF-8 charset and reject query strings and content encoding.

The custom header, JSON-only parsing, and the default `SameSite=Strict` management cookie form a
limited browser CSRF mitigation for this entry set. They prevent a simple cross-site HTML form from
issuing a conforming request. They do not replace a correct CORS policy, trusted-origin validation,
TLS, proxy configuration, or protection from compromised same-origin script. Consumers that loosen
SameSite or allow cross-origin custom headers own the resulting policy.

Request cancellation has priority at every adapter boundary. The original `RequestAborted` token is
passed through and an `OperationCanceledException` carrying that token is not converted to a fixed
HTTP error. An endpoint-owned timeout or an implementation's unrelated cancellation is an internal
unavailable response. A synchronous or non-cooperative consumer component cannot be forcibly
terminated; no hard wall-clock bound is promised for it.

## Installation status (#94)

`GET` and `HEAD {v1}/status` are anonymous in every phase. The Phase Gate deliberately does not read
a snapshot for this entry. The handler reads the consumer health source once, reads the local
`BootstrapConfigurationManager` status once, and reads one process-local restart-required latch.
It emits a success only for a coherent combination:

- `BootstrapConfiguration` may have no Bootstrap file, or may have a valid file only when the local
  restart latch is true after a successful create;
- `PendingSetup` and `Completed` require a valid Bootstrap file;
- a damaged/inaccessible Bootstrap file, missing/null/invalid health snapshot, impossible
  combination, internal exception, or internal timeout is unavailable.

The success body contains exactly `phase`, `migrationStatus`, `databaseStatus`,
`bootstrapConfigured`, and `restartRequired`. Enum values are fixed lower snake case. It does not
serialize `BootstrapManagementStatus` and therefore does not expose ServiceId, InstanceId, provider,
server version, connection string, MasterKey, paths, or source error codes. `HEAD` returns the same
status and headers as `GET` with no body.

| Status outcome | HTTP result | Side effects |
| --- | --- | --- |
| Coherent finite observation | `200 application/json` with the five fixed fields | None |
| Source/store failure, invalid value, inconsistent transition, or internal timeout | `503 {"errorCode":"management.status.unavailable"}` | None |
| Caller cancellation | Original cancellation | None |

The result is a coherent observation assembled during one handler call, not a lock over future
phase changes. The restart latch is initially false, becomes true only after this process successfully
creates or replaces Bootstrap, and resets on process restart. It does not claim that another process
has restarted or that configuration has been activated elsewhere.

## Bootstrap create and update (#95)

Both operations accept a complete, strictly parsed Bootstrap candidate. The raw body limit is 64 KiB
(65536 bytes) and JSON depth is at most 8. Unknown or duplicate properties, malformed UTF-8, a byte
order mark, extra top-level values, query strings, content encoding, and non-JSON media types are
invalid. Connection strings and MasterKeys exist only in the body and the existing input objects;
responses and ServiceMantle-owned diagnostics use closed values.

The wire shape is lower camel case and is independent of the PascalCase file on disk. The top level
accepts exactly `database` and `masterKey`; `database` accepts exactly `provider`,
`connectionString`, and `serverVersion`. `serviceId`, `instanceId`, `formatVersion`, `path`, and
`restartRequired` are not request fields. `POST` requires both top-level properties; `PUT` requires
at least one, retains what it does not send, and treats a supplied `database` as a complete
replacement rather than a merge. An explicit `null` or blank `masterKey` is invalid on both, so
"retain the old value" is never expressed by a value that reached the wire. The full field, value,
and result rules live in
[the Bootstrap management contract](management-bootstrap.md).

### First creation

`POST {v1}/bootstrap` is anonymous only because it requires the independent credential from #324 in
one `X-ServiceMantle-Bootstrap-Credential` header. It never accepts a Setup Code, management cookie,
database password, or MasterKey as that credential. Exactly one header value is read, exactly as it
arrived. Missing, malformed, repeated, comma-combined, expired, unknown, already consumed, and
mismatched credentials share one `401` response:

```json
{"errorCode":"management.bootstrap.credential_invalid"}
```

After the Gate, rate limiter, unsafe-request header, request parser, and structural candidate checks
succeed, the endpoint consumes the credential before calling
`BootstrapConfigurationManager.CreateAsync`. The high-entropy credential is compared in fixed time
and acquired with an atomic local-file operation. An invalid HTTP or structural candidate does not
consume it. Once a structurally valid candidate reaches consumption, later provider validation,
I/O, cancellation, process failure, or a lost response leaves it consumed.

This consume-first ordering prevents credential replay but cannot atomically span the credential and
Bootstrap files. A crash after consumption and before Bootstrap publication may leave neither a
usable credential nor a Bootstrap file; local operations must explicitly provision a new credential.
A lost response after successful publication is recovered through the status endpoint: retrying the
credential fails, while status reports Bootstrap configured and restart required.

Successful creation returns `201 {"restartRequired":true}` only after the Bootstrap file is durably
published, then sets the process-local restart latch. It returns no credential or configuration
value.

### Installed update

`PUT {v1}/bootstrap` is never anonymous and never accepts the Bootstrap credential. Its mapping adds
the fixed management cookie session policy to the existing Admin policy, so its authorization
conclusion comes from this host's own management cookie and not from whatever default scheme a
consuming service configured; mapping it without that scheme registered fails before the host
starts. With a Ready Gate snapshot it then calls `BootstrapConfigurationManager.UpdateAsync`.
Success returns `200 {"restartRequired":true}` after atomic replacement and sets the same latch. A
phase flip after Gate admission does not undo a completed local replacement.

| Bootstrap outcome | HTTP result |
| --- | --- |
| Invalid media, shape, size, unsafe header, or an ordinary candidate rejection | Fixed management 400 |
| Create target already exists, or update target is missing | Fixed management 409 |
| Invalid first-create credential | Fixed credential 401 |
| File/store/internal failure, an internal validator failure, or an internal timeout | `503 {"errorCode":"management.bootstrap.unavailable"}` |
| Successful create | `201 {"restartRequired":true}` |
| Successful update | `200 {"restartRequired":true}` |

The 400/503 split among validator failures is by code only, never by message: the four internal
codes `candidate.validation_failed`, `candidate.invalid_result`, `database.provider_invalid_result`,
and `database.provider_validation_failed` are storage failures, and every other validator failure is
a rejected request. The 409/503 split is the store's own `BootstrapFileFailureKind`
(`TargetAlreadyExists` and `TargetMissing` are the conflict); a message, a path, an inner exception,
or a separate existence check is never consulted. No code, message, connection string, MasterKey,
credential, provider, or server version reaches a response.

The restart latch is set as soon as the manager confirms the file was published, before a later
cancellation or a failed response is considered, so a process never claims the file is unchanged
after it wrote one. A failure before publication sets nothing. At every call, return, and exception
observation point an already aborted request wins over the boundary outcome and propagates its own
`RequestAborted` token rather than a fixed result.

File publication remains the existing per-instance atomic create/replace guarantee. It is not a
cross-instance update, active configuration reload, database transaction, or atomic transaction with
credential consumption, and it adds no compare-and-swap between concurrent updates.

## Setup status and completion (#96)

`GET` and `HEAD {v1}/setup` reveal only `{"status":"pending"}` or
`{"status":"completed"}` from the current installation authority. They expose no Setup Code
generation, digest, issuance/expiry time, operator, contributor, or stored setup value. Bootstrap
phase is rejected by the Gate; Completed remains readable for a stable terminal result. `HEAD` has
no body.

`POST {v1}/setup` accepts exactly one JSON `code` string under a 4 KiB raw-body limit and JSON depth
4. The value must be exactly the existing 32-character, case-sensitive Base64URL Setup Code; it is
never trimmed. In `Completed`, the handler returns the fixed management conflict without parsing or
validating a supplied code. In `PendingSetup`, missing, malformed, expired, mismatched, never-issued,
or replayed code results use one response and disclose no distinction:

```json
{"errorCode":"management.setup.credential_invalid"}
```

The endpoint requires an explicit consumer transaction executor using a fresh scope and clean
DbContext. It performs a read-only code validation, invokes `ServiceSetupOrchestrator` on the clean
staging scope, calls `StageConsumeAsync`, saves all contributor and installation changes once, and
commits. It returns success only after commit. If the code loses a race after contributor staging,
or if any validation, staging, save, commit, cancellation, or cleanup fails, the executor rolls back
and discards the entire scope without retrying. A core orchestrator success remains “staged,” not
“committed.”

| Setup outcome | HTTP result |
| --- | --- |
| Pending or Completed status read | `200` fixed status body |
| Invalid media, body, unsafe header, or contributor validation | Fixed management 400 |
| Invalid/expired/missing/replayed Setup Code while pending | Fixed credential 401 |
| Already Completed, concurrent completion, or version conflict | Fixed management 409 |
| Store/orchestrator/save/commit/cleanup failure or internal timeout | `503 {"errorCode":"management.setup.unavailable"}` |
| Commit confirmed | `204`, empty body |

The consumer transaction owns rollback and disposal. A failed cleanup makes the context unusable and
still returns the fixed unavailable result. Database rollback cannot restore external side effects
of a non-conforming contributor; contributors retain their existing staging-only contract.

## Login, logout, and current session (#97)

`POST {v1}/session/login` is anonymous but phase-gated. ServiceMantle does not define a universal
credential DTO. The required endpoint-specific login adapter receives `HttpContext` and the original
request token, parses a consumer-supported representation under a 64 KiB raw-body limit, stores it
only in the consumer's trusted scoped credential accessor, and calls
`ManagementIdentityProviderInvoker`. The public core provider SPI continues to accept no arbitrary
credential object. The adapter must not retain or return raw credentials.

An authenticated provider result is converted to a ServiceMantle claims principal and passed to
`SignInAsync` for the fixed management cookie scheme. Only completion of `SignInAsync` produces
`204`; failure is unavailable and sends no partial cookie. Unauthenticated results use the existing
session 401. Failed, null, exception, internally cancelled, timed-out, or invalid provider results
use `503 {"errorCode":"management.session.unavailable"}`. Consumer-provided source error codes are
never copied to HTTP or ServiceMantle diagnostics because syntactic validity does not prove that they
are non-secret.

`POST {v1}/session/logout` requires a valid ServiceMantle identity but not Admin. It calls
`SignOutAsync` for the fixed scheme and returns `204` only after the response cookie deletion is
prepared. Logout is local and stateless: it does not revoke copied tickets or invalidate cookies on
another client. Clients may also delete their local cookie when the service is not Ready and the
Gate therefore returns 503.

`GET` and `HEAD {v1}/session` also require a valid identity but not Admin. Success returns exactly
`authenticated`, `expiresAtUtc`, and a stable sorted array of defined permission names. It omits
operator ID/display name/source, claims, authentication properties, ticket material, and provider
data. `HEAD` has no body. Missing, expired, corrupted, or invalid-claim cookies retain the existing
401/403 contract.

| Session outcome | HTTP result | Cookie effect |
| --- | --- | --- |
| Login authenticated and sign-in completed | `204` | One fixed-scheme cookie is issued |
| Login unauthenticated | Existing session 401 | None |
| Login provider/adapter/internal failure or timeout | Fixed session 503 | None |
| Logout with a valid identity | `204` | Current response expires the host-scoped cookie |
| Current-session read with a valid identity | `200` fixed projection | Existing sliding-renewal behavior may apply |
| Missing/expired/corrupt cookie | Existing session 401 | Existing Cookie handler behavior |
| Invalid claims | Existing session 403 | None |

## Required implementation ownership

Issue #323 implements the shared entry kinds, method-aware Phase Gate, exact authentication/rate
metadata, unsafe-request header, and startup anti-downgrade checks. Issue #324 implements the local
one-time Bootstrap credential. Neither issue maps a business endpoint.

Issues #94, #95, #96, and #97 each own only their rows above, their strict parser/result adapters,
and their success, failure, cancellation, security, and concurrency tests. They must retain their
GitHub native dependencies on #312 and #323; #95 additionally depends on #94 and #324. No endpoint
task may copy a shared Gate or credential implementation into its own PR.

## Explicit non-guarantees

- The status observation does not freeze phase, Bootstrap, migration, database, or restart state
  after its one coherent sample.
- Bootstrap credential consumption and Bootstrap file publication are not one atomic transaction;
  consume-first failure requires explicit local recovery.
- Setup commit cannot roll back an external side effect performed by a non-conforming contributor.
- Cookie logout does not provide a server-side revocation authority and cannot invalidate copied
  tickets. Existing expiration and Data Protection boundaries remain unchanged.
- The fixed unsafe-request header is not a global CSRF, CORS, TLS, proxy, firewall, or DDoS policy.
- ServiceMantle's negative credential guarantee covers its parsers, fixed responses, projections,
  and diagnostics. It does not cover consumer adapters, third-party request logging, raw request
  capture, external identity systems, a debugger, or process memory.
- Provider names, server versions, provider error codes, and syntactically valid consumer strings
  are not presumed safe and are not automatically serialized.

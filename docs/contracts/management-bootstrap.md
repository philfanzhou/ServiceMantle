# Bootstrap management endpoint contract

The Bootstrap management entries are the two HTTP operations that write this instance's own local
Bootstrap file: the anonymous first creation, authorized by a one-time local credential, and the
administrator update of an already installed instance.

They implement no file format, no validation rule, and no authorization capability of their own.
They order the existing ones - the shared management entry baseline, the instance-local
`BootstrapFileStore` and its `BootstrapConfigurationManager`, the one-time credential store, and the
process-local restart latch - and project their finite outcomes onto fixed responses.

## Wiring

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddServiceMantleManagementApiV1()
    .AddManagementCookieAuthentication()
    .AddSecurityResponseHeaders()
    .AddRateLimiting()
    .AddServiceMantleBootstrapManagement();

// The consuming service registers the credential store explicitly.
builder.Services.AddSingleton<IBootstrapCredentialStore>(
    new BootstrapCredentialFileStore(serviceId));

var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapServiceMantleBootstrap();
```

`AddServiceMantleBootstrapManagement` adds the shared management entry convention, shares the one
process-local restart latch with the installation status entry, and adds
`X-ServiceMantle-Bootstrap-Credential` to the denied header names so its value can never reach a
ServiceMantle log line. It deliberately registers no `IBootstrapCredentialStore`: ServiceMantle
never provisions a credential on a consumer's behalf, and no HTTP request configures where the
credential is stored.

The host fails to start when the group is mapped more than once, when no `IBootstrapCredentialStore`
is registered, or when the fixed management cookie authentication scheme the update entry resolves
through is missing. A host that does not map this group gains none of those prerequisites.

## The two entries

| Entry | Method and path | Admitted phase | Authorization | Rate limit |
| --- | --- | --- | --- | --- |
| Creation | `POST {v1}/bootstrap` | `BootstrapConfiguration` | Anonymous transport plus one one-time credential header | Setup policy |
| Update | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementAdmin` **and** `ServiceMantle.ManagementSession`, so the fixed management cookie | Management policy |

Both are direct children of the versioned root, are mapped beside the protected
`MapServiceMantleManagementApiV1` group rather than inside it, carry the security response headers,
and require exactly one `X-ServiceMantle-Request: 1` header. The update entry never reads the
creation credential and is never authorized by one.

## The request

Both methods accept only `application/json`, optionally with one UTF-8 `charset` parameter. A query
string, a `Content-Encoding` header, any other media type, a byte order mark, invalid UTF-8, a
non-object root, an extra top-level value, a comment, a trailing comma, and a JSON nesting depth
above 8 are all rejected. The raw body is at most 65536 bytes, counted before any decoding: a larger
`Content-Length` is rejected without reading, and a body with no declared length is read to at most
one byte past the limit, which is enough to prove it is oversized.

The wire shape is lower camel case and is independent of the PascalCase file on disk.

| Property | Type | Rule |
| --- | --- | --- |
| `database` | object | Required by `POST`. On `PUT` it is a complete replacement, never a merge. |
| `database.provider` | string | Required, non-blank. Trimmed, 1-64 characters, starts with an ASCII letter or digit, then letters, digits, `.`, `-`, or `_`. |
| `database.connectionString` | string | Required, non-blank. Trimmed by the existing core rule. |
| `database.serverVersion` | string or null | Optional. Omitted and explicit `null` both mean absent; an explicit blank string is rejected. |
| `masterKey` | string | Required by `POST`, non-blank. Trimmed by the existing core rule. |

`POST` requires both `database` and `masterKey`. `PUT` requires at least one of them and retains
what it does not send. An explicit `null` or blank `masterKey` is rejected on both, so the core's
"blank means retain" rule never makes the HTTP meaning ambiguous. Unknown, differently cased, and
disk-only names - `serviceId`, `instanceId`, `formatVersion`, `path`, `restartRequired` - are
rejected, and so is a duplicate, including one written with an escape that decodes to the same name.

The structural check only builds the existing request values and applies the rules above. It calls
no provider. Whether a provider is registered, whether it requires a server version, and whether the
database is reachable are decided afterwards by the manager's existing validator - for the creation,
only after the credential has been consumed.

## The credential

The creation entry is anonymous only because it carries the one-time credential from the credential
contract in exactly one `X-ServiceMantle-Bootstrap-Credential` header, exactly as it arrived: 43
Base64URL characters, never trimmed or normalized. It is never a Setup Code, a management cookie, a
database password, or a MasterKey.

A missing, malformed, repeated, or comma-combined header value, and a candidate the store reports as
invalid - expired, unknown, mismatched, or already consumed - all share one response:

```json
{"errorCode":"management.bootstrap.credential_invalid"}
```

A store failure is not that: an unavailable store, a null result, and an exception are
`503 {"errorCode":"management.bootstrap.unavailable"}`.

## Order and results

1. the shared phase gate;
2. authentication, rate limiting, and authorization;
3. the `X-ServiceMantle-Request` guard;
4. the HTTP and structural parser;
5. for `POST`, the credential's shape, then its consumption;
6. the manager's provider validation and file publication;
7. the restart latch;
8. one fixed response.

Everything before step 5 rejects without consuming anything and without writing anything. Only a
consumed credential reaches `CreateAsync`.

| Outcome | HTTP result |
| --- | --- |
| Published creation | `201 {"restartRequired":true}` |
| Published update | `200 {"restartRequired":true}` |
| Unusable media, shape, size, depth, field, value, or unsafe header | Fixed management `400` |
| An ordinary candidate rejection from the validator | Fixed management `400` |
| An unusable creation credential | `401 {"errorCode":"management.bootstrap.credential_invalid"}` |
| `BootstrapFileFailureKind.TargetAlreadyExists` or `TargetMissing` | Fixed management `409` |
| `BootstrapFileFailureKind.Unavailable` or an unrecognized value | `503 {"errorCode":"management.bootstrap.unavailable"}` |
| An internal validator failure, another internal failure, an internal cancellation, or an internal timeout | `503 {"errorCode":"management.bootstrap.unavailable"}` |

The 400/503 split among validator failures is by code only. The four internal codes
`candidate.validation_failed`, `candidate.invalid_result`, `database.provider_invalid_result`, and
`database.provider_validation_failed` are storage failures; every other validator failure is a
rejected request. The code itself is compared and never written to a response, a message, or a log.

The 409/503 split is the store's own classification. A message, a path, an inner exception, an
`HResult`, and a separate existence check are never consulted.

## The restart latch

The latch is set as soon as the manager confirms the file was published, before a later cancellation
or a failed response is considered. A failure before publication sets nothing; a publication whose
response is lost keeps the latch, so the process never claims the file is unchanged after it wrote
one. The latch is per process: it is never persisted and a new process starts with `false` until it
writes again. The installation status entry reports the same latch.

## Cancellation

At every call, return, and exception observation point the caller's own cancellation outranks what
the boundary produced. When `RequestAborted` is already cancelled the handler throws an
`OperationCanceledException` carrying exactly that token and returns no result, whatever the store,
the parser, or the manager answered - including an internal cancellation carrying a different token.
The caller's token is passed unchanged to the credential store and to the manager; nothing is
abandoned to a background task while a write continues. When the request was not cancelled, an
internal cancellation, an internal timeout, and an ordinary failure stay the one fixed unavailable
result. This group introduces no endpoint-wide timeout of its own.

## Explicit non-guarantees

- Credential consumption and file publication are two files and not one transaction. A caller or
  process cancellation does not roll back a published file, a lost response does not restore a
  consumed credential, and recovery is a local operations action.
- Only this instance is changed. Nothing is hot-reloaded, synchronized across instances, or
  migrated, and a restart is what activates the file.
- Synchronous and uncooperative I/O has no hard time bound, and gate admission does not lock the
  phases that follow.
- Concurrent updates from different processes get no compare-and-swap. Outside exclusive handles,
  ACLs, and file replacement keep the core store's existing non-guarantees.
- The secret guarantee covers this group's own outputs and the existing ServiceMantle projector
  only. A consumer's own validator or credential store, third-party request capture, an arbitrary
  proxy or framework log, process memory, and a debugger are outside it.
- The `X-ServiceMantle-Request` header is a limited CSRF mitigation for this entry set, not a
  replacement for CORS, TLS, or a proxy policy.

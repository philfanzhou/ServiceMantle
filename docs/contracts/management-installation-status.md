# Installation status entry contract (#94)

Status: implemented. This document describes the anonymous installation status entry of the
ServiceMantle management surface. It refines the Installation status row of
[management-entry-authorization.md](management-entry-authorization.md) and adds nothing to it.

## Registration and mapping

`AddServiceMantleInstallationStatus` registers the process-local Bootstrap restart latch and the
Bootstrap observation boundary the entry reads. It also adds the shared management entry convention,
which the entry consumes and never extends. `MapServiceMantleInstallationStatus` maps the entry:

```csharp
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddServiceMantleManagementApiV1()
    .AddServiceMantleInstallationStatus();

var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapServiceMantleInstallationStatus();
```

The entry is a direct child of the configured versioned root, mapped beside the protected group
returned by `MapServiceMantleManagementApiV1` and never inside it. With the default root the full
path is `/management/v1/status`; a custom versioned root moves it. It is mapped at most once. A
missing installation status capability, management API v1 capability, or shared management entry
capability, and a second mapping, all fail before the host starts.

`GET` and `HEAD` are the only methods this entry owns. The shared entry baseline stops every other
method before any handler with the existing `503 {"errorCode":"service.phase.unavailable"}`.

## Admission and security baseline

The entry is anonymous in every defined phase and in every migration and database state, so the
Phase Gate admits it without reading a health snapshot. It keeps the shared baseline unchanged: the
management rate-limit policy with the anonymous client partition, so a presented management cookie
cannot move the entry into a per-operator quota; the mandatory security response headers, including
`Cache-Control: no-store`; and the existing correlation contract. A rate-limit rejection keeps the
existing `429 application/problem+json` response and never enters the handler.

## Observation

The handler parses nothing. It declares no route value, query parameter, header, or request body. It
reads, at most once each and in this order:

1. the consuming service's `IServiceHealthSnapshotSource`, bounded by the management API's own
   `SnapshotTimeout`;
2. the local `BootstrapConfigurationManager` status, through a boundary that keeps only the
   presence flag;
3. the process-local Bootstrap restart latch.

It performs no write, retry, caching, or background work, and holds no mutable object across
requests. Concurrent callers each assemble their own complete observation.

## Coherence

A success is emitted only for a combination this process could actually be in:

| Phase | Local Bootstrap file | Restart latch | Result |
| --- | --- | --- | --- |
| `BootstrapConfiguration` | Absent | Any | Success |
| `BootstrapConfiguration` | Valid | `true` | Success |
| `BootstrapConfiguration` | Valid | `false` | Unavailable |
| `PendingSetup`, `Completed` | Valid | Any | Success |
| `PendingSetup`, `Completed` | Absent | Any | Unavailable |
| Any | Damaged, mismatched, unreadable | Any | Unavailable |

Migration and database states place no further restriction: every defined value is projected.

## Responses

| Outcome | HTTP result |
| --- | --- |
| Coherent finite observation | `200 application/json` with the five fixed fields |
| Absent, null, or failing snapshot source | `503 {"errorCode":"management.status.unavailable"}` |
| Damaged, mismatched, or unreadable Bootstrap file | Same `503` |
| Incoherent combination | Same `503` |
| Internal failure, internal cancellation, or internal timeout | Same `503` |
| Caller cancellation | The original `RequestAborted` token propagates |

The success body contains exactly `phase`, `migrationStatus`, `databaseStatus`,
`bootstrapConfigured`, and `restartRequired`, written field by field:

```json
{
  "phase": "completed",
  "migrationStatus": "succeeded",
  "databaseStatus": "reachable",
  "bootstrapConfigured": true,
  "restartRequired": false
}
```

Enumeration values are fixed lower snake case: `bootstrap_configuration`, `pending_setup`,
`completed`; `not_started`, `running`, `succeeded`, `failed`; `reachable`, `unreachable`. A value
outside those enumerations is unavailable, never guessed.

`HEAD` answers the same status code and the same headers, including `Content-Length`, with no body.

## Negative disclosure guarantee

No snapshot, `BootstrapManagementStatus`, configuration, path, or exception object reaches a
serializer. The response, the error body, and ServiceMantle's own diagnostics therefore carry no
ServiceId, InstanceId, database provider, server version, connection string, MasterKey, Bootstrap
file path, health source error code, or exception text. The unavailable body carries the closed
error code and nothing else, so a store failure, an incoherent combination, and an internal timeout
are indistinguishable to an anonymous caller.

## The restart latch

The latch is per process. It starts `false`, is set only after this process successfully creates or
replaces its own local Bootstrap file, is never persisted, and resets on restart. It never claims
that another instance restarted, that another process wrote Bootstrap, or that configuration was
activated anywhere.

## Explicit non-guarantees

- The projection is one coherent observation assembled during one call. It does not freeze the
  phase, the Bootstrap file, migration, the database, or the latch after the response is written.
- The internal budget bounds waiting after the health source yields. The local Bootstrap read is
  synchronous file access; it is checked against the budget after it completes and is not preempted.
  A non-cooperative consumer source cannot be forcibly terminated.
- The anonymous disclosure guarantee covers ServiceMantle's own projection, fixed responses, and
  diagnostics. It does not cover third-party request logging, raw request capture, or a consuming
  component that places a secret into a value outside the five fixed fields.
- The entry creates, updates, and returns no Bootstrap configuration and implements no Setup, login,
  cookie, or management write.

# Management runtime information endpoint contract

`MapServiceMantleRuntimeInfo` adds one read-only endpoint to the protected management API v1 group:
`GET /runtime`. It answers who is serving the request — the registered service name, service
version and instance identity — plus the startup phase the request was admitted in. It is the
smallest possible identity endpoint: it reads nothing, caches nothing, and exposes no diagnostic,
configuration or environment detail.

This document describes what the endpoint fixes, what the consuming service still owns, and what is
explicitly not covered. The surrounding baseline is described in
[the protected management API v1 contract](management-api-v1.md).

## Wiring

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddManagementCookieAuthentication()
    .AddServiceMantleManagementApiV1();

builder.Services.AddSingleton<IServiceHealthSnapshotSource, ProductSnapshotSource>();

var app = builder.Build();
app.UseServiceMantlePipeline();

var management = app.MapServiceMantleManagementApiV1();
management.MapServiceMantleRuntimeInfo();
```

The endpoint is opt-in. `AddServiceMantleManagementApiV1` and `MapServiceMantleManagementApiV1` never
add it, so a host that does not call `MapServiceMantleRuntimeInfo` exposes nothing here. There is no
separate `Add…` registration: the identity the endpoint projects is the one `AddServiceMantle`
already registered.

| Setting | Value |
| --- | --- |
| Route | `GET /runtime`, relative to the versioned management root |
| Default full path | `/management/v1/runtime` |
| Custom root | Moves with the group, for example `/ops/admin/v1/runtime` |
| Parameters | None. The endpoint declares no route, query, header or body parameter |

Mapping the endpoint more than once per host, and mapping it onto any route group other than the one
returned by `MapServiceMantleManagementApiV1`, fail before the host starts with one fixed message
that never repeats a configured value. A nested group below the management group is rejected the
same way, so the full path stays exactly the versioned root plus `/runtime`.

## Response

```json
{
  "serviceName": "catalog",
  "serviceVersion": "1.4.2",
  "instanceId": "catalog-01",
  "phase": "Completed"
}
```

`200 application/json` with exactly these four fields, in this order.

| Field | Source |
| --- | --- |
| `serviceName` | `ServiceLogContext.ServiceName`, fixed by `AddServiceMantle` |
| `serviceVersion` | `ServiceLogContext.ServiceVersion`, fixed by `AddServiceMantle` |
| `instanceId` | `ServiceLogContext.InstanceId`, fixed by `AddServiceMantle` |
| `phase` | The constant `Completed` |

The three identity values come from the immutable `ServiceLogContext`; the body is built once when
the endpoint is mapped, so no request, configuration, Bootstrap, environment, exception or logging
object ever reaches a serializer. There is no extension point that could add a field.

`phase` is not a second reading of the health state. The phase gate admits a normal management
request only in `Completed` + `Succeeded` + `Reachable`, so an admitted request has already observed
that phase, and the handler reports it as a constant rather than querying the snapshot source, the
database or a cache again. Each request therefore reads the snapshot source exactly once, in the
gate.

## Rejections

Every rejection is the existing group baseline, unchanged, and none of them runs the projection.

| Situation | Status | Body |
| --- | --- | --- |
| Phase, migration or database state other than `Completed` + `Succeeded` + `Reachable`; missing, failing, `null`, internally cancelled or timed-out snapshot source | 503 | `{"errorCode":"service.phase.unavailable"}` |
| A method other than `GET` on the runtime path | 503 | `{"errorCode":"service.phase.unavailable"}` |
| No management session presented | 401 | `{"errorCode":"management.session.unauthenticated"}` |
| Presented session cannot be accepted | 401 | `{"errorCode":"management.session.expired"}` |
| Authenticated principal without `Admin` | 403 | `{"errorCode":"management.session.forbidden"}` |
| Rate-limit quota exhausted | 429 | Problem Details, `rate_limit.exceeded` |

Answers carry the six mandatory security response headers, including `Cache-Control: no-store`, and
exactly one `x-correlation-id`. The endpoint adds no caching, `ETag`, background polling or I/O of
its own. Bodies are identical in Development and Production.

Caller cancellation stays caller cancellation: an already cancelled request never reads the snapshot
source, and a cancellation during the gate's observation surfaces as the caller's own cancellation
rather than a 503 or a 500. Concurrent requests share neither their Correlation ID nor their
cancellation.

## Consumer responsibilities

- The service name, version and instance identity are supplied by the consuming service and may be
  free text. They are published verbatim to any `Admin` operator, so they must not contain secrets.
- Provide one truthful `IServiceHealthSnapshotSource`, the default authentication schemes and the
  login flow, exactly as the management API v1 baseline requires.
- Keep secrets out of Correlation IDs. A well-formed caller value is reused verbatim.

## Not included and not guaranteed

- No pre-installation or failed-phase diagnostics, configuration values, connection information,
  credentials, runtime environment inventory, extension fields or product diagnostics. Reading state
  before installation completes is owned by its own task and is **not** delivered here.
- `phase` describes the moment the request was admitted. It is not a promise about the moment the
  response is written, about another request, or about another instance. A state change after
  admission does not retract an admitted request, and there is no cross-request or cross-instance
  atomic observation. No business readiness contributor is executed.
- Secret-free output is asserted for this response body, for the fixed rejections above, and for this
  endpoint's own diagnostics. A value a consumer places in the publicly declared identity, version or
  a well-formed Correlation ID is not detected or removed; neither are replaced services or policies,
  third-party logging, process memory, or anything after the response has started.
- No new cache, `ETag`, background refresh, I/O, database access, SQL, package dependency or
  persistence concern is introduced by this endpoint.

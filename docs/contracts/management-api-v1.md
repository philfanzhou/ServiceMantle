# Protected management API v1 contract

`AddServiceMantleManagementApiV1` and `MapServiceMantleManagementApiV1` combine capabilities that
ServiceMantle already ships — the startup phase gate, management authorization, the named management
rate-limit policy, and the mandatory security response headers — into one opt-in, versioned route
group. The entry point is a baseline for management endpoints a consuming service writes itself. It
is not a new pipeline, and it publishes no endpoint of its own.

This document describes what the baseline fixes, what the consuming service still owns, and what is
explicitly not covered.

## Minimal wiring

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
management.MapGet("/settings", ReadSettings);
management.MapPost("/settings", UpdateSettings);
```

`AddServiceMantleManagementApiV1` registers the phase gate on the versioned root and the
`ServiceMantle.ManagementAdmin` policy. It does **not** register cookie authentication, an identity
provider, persistence, health endpoints, or any other optional capability. The consuming service
registers the security response headers, the sensitive Header registry and the rate-limit policies,
supplies default authenticate, challenge and forbid schemes, and composes the request pipeline with
`UseServiceMantlePipeline`. `AddManagementCookieAuthentication` is one way to supply those schemes;
an external authentication handler that produces a conforming principal is equally acceptable.

## Root, version, and registration

| Setting | Value |
| --- | --- |
| Default root | `/management/v1` |
| Configurable | Any root whose final segment is exactly `v1`, for example `/ops/admin/v1` |
| Normalization | Owned by the existing phase gate: lower-cased, trailing slash removed, 2–128 characters, `[a-z0-9_-]` segments, no `/health` overlap |
| Versioning | Path only. There is no query, header, or content negotiation and no second version |

The configured root is also the phase gate's management prefix, so the group shares one namespace
with `MapServiceMantleManagementGroup` rather than creating a second classifier. A host that also
calls `AddServiceMantlePhaseGate` must use settings that normalize to exactly the same prefix and
snapshot timeout. Repeating an equivalent `AddServiceMantleManagementApiV1` registration is
idempotent. The existing `AddServiceMantle`, `AddServiceMantlePhaseGate` and
`MapServiceMantleManagementGroup` entry points keep their own defaults and semantics, including the
`/management` phase-gate default in hosts that do not use this entry point.

## What the group fixes

Every child mapped into the returned group receives, as endpoint metadata:

- the `ManagementSurface.Management` surface,
- the `ServiceMantle.ManagementAdmin` authorization policy,
- the `servicemantle.management` rate-limit policy,
- the mandatory security response-header baseline.

A child may add a **stricter** authorization requirement on top of the baseline. Making a child (or
the group) anonymous, disabling or replacing its rate-limit policy, removing its security-header
marker or its baseline policy, and adding a second management surface are all rejected before the
host starts. So are an invalid or unversioned root, a conflicting registration or phase gate, a
missing required capability, missing default authentication schemes, a host that did not compose
`UseServiceMantlePipeline`, and mapping the group more than once per host. Configuration errors
carry a fixed message and never repeat the configured value.

The `/status`, `/bootstrap` and `/setup` branches below the root stay reserved for their own
surfaces and cannot be served from this group. Endpoints a host maps outside the group — including
ones mapped with the older phase-gate API under the same root — are not implicitly taken over.

## Request order and response matrix

The composed pipeline runs routing, the security response headers, the phase gate, authentication,
rate limiting, and authorization in that order, so a rejection from an earlier stage answers first.

| Situation | Status | Body |
| --- | --- | --- |
| Phase, migration or database state other than `Completed` + `Succeeded` + `Reachable`; missing, failing, `null`, internally cancelled or timed-out snapshot source | 503 | `{"errorCode":"service.phase.unavailable"}` |
| No management session presented | 401 | `{"errorCode":"management.session.unauthenticated"}` |
| Presented session cannot be accepted | 401 | `{"errorCode":"management.session.expired"}` |
| Authenticated principal without `Admin` | 403 | `{"errorCode":"management.session.forbidden"}` |
| Rate-limit quota exhausted | 429 | Problem Details, `rate_limit.exceeded` |
| `ManagementApiResults.InvalidRequest()` | 400 | Problem Details, `management.request.invalid` |
| `ManagementApiResults.Conflict()` | 409 | Problem Details, `management.request.conflict` |
| Unmapped exception, or an internal `OperationCanceledException` | 500 | Problem Details, `http.internal_server_error` |
| Handler result | 2xx | The consuming service's own response |

The 401/403 bodies above hold for the native ServiceMantle management cookie. An external
authentication handler owns its own challenge and forbid body; this entry point does not rewrite it.
Every answered response above carries the six mandatory security headers and exactly one
`x-correlation-id` header while the response has not started.

## The two fixed results

```csharp
management.MapPost("/settings", (SettingsRequest request) =>
    request.IsValid
        ? Results.NoContent()
        : ManagementApiResults.InvalidRequest());
```

Both results are closed. They take no free text, no input value, no exception, and no custom
extension, and their `application/problem+json` body always contains exactly these five fields:

| Field | `InvalidRequest()` | `Conflict()` |
| --- | --- | --- |
| `type` | `urn:servicemantle:error:management.request.invalid` | `urn:servicemantle:error:management.request.conflict` |
| `title` | `The request is invalid.` | `The request conflicts with the current state.` |
| `status` | `400` | `409` |
| `errorCode` | `management.request.invalid` | `management.request.conflict` |
| `correlationId` | The Correlation ID already resolved for this request | Same |

The Correlation ID is read from the request's resolved slot, never from the raw request header, so
it matches the response header. The bodies are identical in Development and Production. If the
handler has already started the response, the sent response is left unchanged and nothing is
appended. Other formats are unchanged: cookie 401/403 and phase-gate 503 stay `{"errorCode": …}`
JSON, and 429 and unmapped exceptions keep their existing Problem Details shape. Arbitrary framework
or consumer 4xx responses are not rewritten into these results.

## Consumer responsibilities

- Provide one truthful `IServiceHealthSnapshotSource`. Its accuracy and freshness are the consuming
  service's responsibility, and a state change after the snapshot does not retract an admitted
  request.
- Provide the default authentication schemes and, where applicable, the login and session flow.
- Write the endpoints themselves, including their input validation, replay protection and
  cross-site request protection.
- Keep secrets out of Correlation IDs. A well-formed caller value is reused verbatim.

## Not included and not guaranteed

- No pre-installation state reading, Bootstrap first-time creation or post-installation update,
  Setup Code HTTP protocol, login, logout or session entry point, and no configuration, audit or
  runtime-information endpoints. Those are owned by their own tasks and are **not** delivered here.
- No reimplementation of the phase gate, Claims parser, cookie handling, forwarded headers, rate
  limiter, sanitizer, or the composed pipeline; no new core, EF Core or provider dependency; no
  database, file, or consumer transaction access.
- The guarantees cover fixed mapping before a successful start, correct use of the existing
  pipeline, and the paths and results above while the response has not started. Replacing routes at
  runtime, bypassing the group, replacing library-owned services or policies, an upstream
  short-circuit, and later transport operations are outside them.
- No cross-request or cross-instance atomic phase transition, and no background health scheduling
  for business contributors.
- Secret-free output is asserted for the fixed results here, the existing unmapped-exception
  fallback, the native cookie, phase gate and rate-limiter errors, and ServiceMantle's own
  diagnostics. Responses a consumer handler writes itself, custom mapping extensions, third-party
  authentication or logging, the raw request, and process memory are not covered.
- No global CSRF, CORS or TLS policy, no DDoS or firewall protection, no distributed rate limiting,
  no multi-version compatibility, and no automatic conversion of arbitrary model-binding errors.

# Management setting query contract

`MapServiceMantleSettingQueries` adds two read-only endpoints to the protected management API v1
group: `GET /settings/definitions` returns the registered setting catalog, and `GET /settings`
returns the current value of every setting in one complete, successfully refreshed version. They are
a thin HTTP adaptation of the existing provider-independent `ServiceSettingQueryService`: they add no
store, no root-key source, no cache, and no write path.

This document describes the fixed projections, the bounded input, the single failure answer, and what
is explicitly not covered. The surrounding baseline is described in
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
builder.Services.AddSingleton<IServiceSettingDefinitionProvider, ProductSettingDefinitions>();
builder.Services.AddSingleton<IServiceSettingStore, ProductSettingStore>();
builder.Services.AddSingleton<IServiceSettingRootKeySource, ProductRootKeySource>();
builder.Services.AddServiceMantleSettingSnapshots();

var app = builder.Build();
app.UseServiceMantlePipeline();

var management = app.MapServiceMantleManagementApiV1();
management.MapServiceMantleSettingQueries();
```

The endpoints are opt-in. `AddServiceMantleManagementApiV1` and `MapServiceMantleManagementApiV1`
never add them, so a host that does not call `MapServiceMantleSettingQueries` exposes nothing here.
The consuming service owns the store or snapshot source, the definition catalog, and — whenever the
catalog contains a sensitive setting — the root-key source; `AddServiceMantleSettingSnapshots` is the
registration that binds them. A host that maps these endpoints without that capability, maps them
more than once, or maps them onto any route group other than the one returned by
`MapServiceMantleManagementApiV1` (including a nested group below it) fails before the host starts
with a fixed message that never repeats a configured value.

| Route | Default full path | Refreshes |
| --- | --- | --- |
| `GET /settings/definitions` | `/management/v1/settings/definitions` | Never |
| `GET /settings` | `/management/v1/settings` | Exactly once per request |

A custom versioned root moves both endpoints with the group. Neither endpoint accepts a write method,
and neither exposes an update, Setup, audit or search surface.

## The `group` query

Both endpoints accept one optional query parameter, spelled exactly `group`.

| Rule | Value |
| --- | --- |
| Occurrences | At most one; a repeated `group` is rejected |
| Other parameters | None. Any other query key is rejected, alone or alongside `group` |
| Raw length | At most 128 characters |
| Normalization | `Trim()` then `ToLowerInvariant()`; the result must be non-empty |
| Syntax | `[a-z0-9][a-z0-9._-]*`, the same shape as a normalized setting key |
| Match | `key == group`, or `key` starts with `group + "."` (ordinal) |

There is no group domain model and no group registry: the value is a filter over keys. An accepted
group that matches nothing returns an empty collection — for current values, together with the
version of the complete refresh that just succeeded. Rejected input answers the fixed management
`400` result (`management.request.invalid`) and never echoes the value.

The filter shapes output only. It never reduces the work of the underlying load, decryption and
validation, and it can never let an unknown key, a corrupt value, or any other failure of the
complete snapshot answer `200` for a different group.

## Definition response

```json
{
  "definitions": [
    {
      "key": "product.retention-days",
      "valueType": "number",
      "isRequired": true,
      "isSensitive": false,
      "hasDefault": true,
      "requiresRestart": false
    }
  ]
}
```

`200 application/json`. Each item carries exactly those six fields, in that order, and the items are
sorted by key in ascending ordinal order. `valueType` is one of `string`, `number`, `boolean`,
`json`.

The catalog is projected from the safe definition projection the core query service already produces.
The response contains no default value and no constraint object — only `hasDefault`. This request
never refreshes the snapshot and therefore never touches the store, the root key, or the network.

## Current-value response

```json
{
  "version": 7,
  "values": [
    {
      "key": "product.retention-days",
      "valueType": "number",
      "isRequired": true,
      "isSensitive": false,
      "hasDefault": true,
      "requiresRestart": false,
      "hasValue": true,
      "source": "persisted",
      "value": "30"
    }
  ]
}
```

`200 application/json`. `version` is the int64 version of the one complete snapshot this request
refreshed; every item comes from that same version. Each item carries exactly those nine fields, in
that order, sorted by key in ascending ordinal order. `source` is one of `missing`, `default`,
`persisted`. A `null` field is always written, never omitted.

`value` is the existing invariant normalization of a non-sensitive value — an invariant decimal for
`number`, `true`/`false` for `boolean`, canonical JSON text for `json` — and is `null` whenever the
value is missing **or** the definition is marked sensitive. That holds for every sensitive value type,
including sensitive `number`, `boolean` and `json`.

## Rejections

| Situation | Status | Body |
| --- | --- | --- |
| Invalid, repeated or unknown query input | 400 | Problem Details, `management.request.invalid` |
| Any refresh failure of the complete snapshot | 503 | `{"errorCode":"management.settings.unavailable"}` |
| Phase gate, session, permission and quota rejections | 503 / 401 / 403 / 429 | The management API v1 baseline, unchanged |
| Unexpected unmapped exception | 500 | Problem Details, `http.internal_server_error` |

The `503` body is closed. It covers every failure classification the existing loader produces —
unknown or duplicate key, mixed or missing version, type mismatch, a sensitive value that is not in a
supported envelope, corrupt ciphertext, a wrong or unavailable root key, a value or composite
constraint failure, a storage error, and a stale or same-version conflicting snapshot — and it never
reports which one, never names a key, and never answers a partial or previously activated value.

Caller cancellation stays caller cancellation: an already cancelled request never refreshes, and a
cancellation while another refresh holds the loader's lock, or during a cooperative read, surfaces as
the caller's own cancellation rather than a `503` or a `500`. Answers carry the six mandatory security
response headers and exactly one `x-correlation-id`, and are identical in Development and Production.

## Consumer responsibilities

- Mark every setting whose value is secret as sensitive. A value that is not marked sensitive is
  readable by design, and this endpoint does not try to detect a secret inside it.
- Own the store, the definition catalog, the root-key source, and the accuracy of the persisted
  version. Keys, identity, version and a well-formed Correlation ID must stay publicly disclosable.
- Own authorization beyond the group's `Admin` baseline, and own writes: this contract has no update
  path.

## Not included and not guaranteed

- No transactional update, Setup flow, audit query, default or constraint detail, paging, search,
  cache or background refresh. Those are owned by their own tasks and are **not** delivered here.
- The guarantee is that a value marked sensitive never reaches this response body, the fixed errors,
  or this endpoint's own diagnostics; that the definition response carries no default and no
  constraint object; and that a failed refresh answers no partial or previous value. It does not
  cover replaced library services or policies, third-party logging, process memory, or anything after
  the response has started.
- Grouping affects output only. There is no bound on catalog size, value size, a blocking source, or
  a synchronous validator, no additional internal query timeout, and only the cooperative cancellation
  the source and loader already provide.
- One response is one version; two requests may observe different versions. A refresh may activate
  the process-local snapshot accessor, which is part of the existing query contract — no database is
  written, no consumer unit of work is committed, and no cross-instance atomic publication is implied.
- A phase change after the gate admitted the request does not retract it, and this contract carries no
  cross-site write protection.

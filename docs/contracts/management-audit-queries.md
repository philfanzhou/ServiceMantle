# Management audit query HTTP contract

`MapServiceMantleAuditQueries()` opts one read-only endpoint into the protected group returned by
`MapServiceMantleManagementApiV1()`:

```text
GET {versionedRoot}/audit
```

The default route is `GET /management/v1/audit`. The endpoint inherits the management phase gate,
administrator authorization, management rate limit, security response headers, and correlation ID
behavior of the v1 group. It must be mapped exactly once as a direct child of that group and requires
a scoped or singleton `IManagementAuditQueryService` registration. It performs one query per accepted
request and never saves or commits the consuming application's unit of work.

## Query input

The exact, case-sensitive query-key allowlist is `action`, `targetType`, `targetId`, `operatorId`,
`fromUtc`, `toUtc`, `page`, `pageSize`, `sortOrder`, and `cursor`. Unknown, repeated, differently cased,
or empty values are rejected with the fixed management `400` response before the query service is
called. A request body is not accepted.

- `page` defaults to `1` and accepts unsigned decimal values from `1` through `int.MaxValue`.
- `pageSize` defaults to `50` and accepts unsigned decimal values from `1` through `200`.
- Page 1 rejects a cursor. Every later page requires the preceding response's cursor.
- `sortOrder` is exactly `newest` (the default) or `oldest`.
- Action, target type, target ID, operator ID, and cursor inputs are length-checked before the core
  audit value types validate and normalize them. The cursor limit is 512 characters.
- Times are invariant ISO-8601 values with required seconds and either `Z` or an explicit `±HH:mm`
  offset. One through seven fractional digits are optional. Each time is at most 40 characters and is
  converted to UTC. When both bounds are present, the inclusive range cannot exceed 366 days.

## Success response

A `200 application/json` response has exactly `items`, `page`, `pageSize`, `totalCount`,
`continuationCursor`, and `hasNextPage`. Each item explicitly contains only `id`, `operator`, `action`,
`target`, `outcome`, `occurredAtUtc`, `clientIp`, `correlationId`, `securityDescription`, and `metadata`.
GUIDs use the `D` form, times are UTC, outcomes are `unknown`, `success`, `failure`, or `denied`, and
nullable fields are written explicitly as `null`. Persistence entities, exceptions, and serialized
metadata storage are never serialized directly.

## Failure and concurrency boundaries

HTTP parsing failures and `audit.query_*` query failures return the fixed management invalid-request
response. `audit.entity_invalid`, other query exceptions, and internal cancellation return
`503 application/json` with exactly `{"errorCode":"management.audit.unavailable"}`. Caller cancellation
remains caller cancellation and does not produce a success or error body.

The endpoint preserves the core query's ordinary keyset semantics. A cursor is a query-bound opaque
continuation value, not a signed authorization token, replay defense, or database snapshot. Total
count can change between requests, and a backfilled record in the remaining ordering interval can
appear on a later page. Input bounds do not impose an absolute bound on database scans, provider
buffering, process allocation, or query duration. Output safety covers the audit domain's supported
sensitive-content formats, explicit response projection, fixed errors, and this endpoint's own
diagnostics; it is not general-purpose secret detection and does not cover third-party logging or a
response that has already started.

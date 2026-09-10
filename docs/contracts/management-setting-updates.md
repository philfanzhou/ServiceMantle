# Management setting update HTTP contract

`MapServiceMantleSettingUpdates(executor)` opts one transactional write endpoint into the protected
group returned by `MapServiceMantleManagementApiV1()`:

```text
POST {versionedRoot}/settings
```

The default route is `POST /management/v1/settings`. It inherits the completed/succeeded/reachable
phase gate, administrator authorization, management rate limit, security response headers, and
correlation ID behavior. The endpoint is mapped exactly once as a direct child of the v1 group.

The required `SettingUpdateExecutor` is the consumer-owned commit boundary. It resolves
a fresh scoped unit of work, begins a transaction, calls `ServiceSettingUpdateService`, commits only
an applied result, and returns Applied only after that commit completes. Failure and cancellation
roll back and discard the scope without retrying. The endpoint never accesses a `DbContext`, commits
an existing consumer unit of work, or changes the core meaning of Applied (saved but not committed).

## Request

The endpoint accepts only `application/json` with no query string, content encoding, or request body
larger than 256 KiB. An optional charset must be UTF-8. JSON depth is at most 8. The exact body is:

```json
{
  "expectedVersion": 7,
  "changes": [
    { "key": "product.name", "value": "Orders" },
    { "key": "product.optional", "value": null }
  ]
}
```

Both top-level properties are required and case-sensitive; unknown or duplicate properties are
rejected. `expectedVersion` is an integer from zero through `long.MaxValue`. `changes` contains one
through 32 objects with exactly `key` and `value`. Each raw key is one through 128 characters and
must remain non-empty after trimming. Keys that collide after trimming and case-insensitive
comparison are rejected. A value is a JSON string or `null`; strings are passed unchanged and null
removes the explicit persisted value. The existing updater validates registered keys and the full
candidate, including string, number, Boolean, JSON, required/default, sensitive-value, and composite
constraints. Request data never selects the audit operator; it is resolved from the authenticated
principal by `IManagementCurrentOperatorResolver`.

## Responses and boundaries

- Applied with a positive committed version returns `200 application/json` and exactly
  `{"version":N}`.
- Format and validation failures return the fixed management `400`.
- Version conflict or exhaustion returns the fixed management `409`.
- Protection, storage, transaction, context, malformed-result, exception, and internal-cancellation
  failures return `503 application/json` and exactly
  `{"errorCode":"management.settings.update_unavailable"}`.
- An unresolved current operator invokes the configured forbid scheme without executing the update.
- Caller cancellation remains caller cancellation. Cancellation after a confirmed commit does not
  imply that the database commit can be undone.

No response includes validation details, keys, values, ciphertext, or exception text. This endpoint
does not add cross-request idempotency, automatic conflict retries, snapshot publication, database
performance bounds, compensation for an unknown commit outcome, or a complete CSRF/CORS/TLS policy.
Its negative secret guarantee covers this endpoint's response bodies, fixed errors, and diagnostics
plus the updater's existing key-only audit behavior; it does not cover third-party request logging,
raw request retention, process memory, consumer exception sinks, or secret-like registered keys.

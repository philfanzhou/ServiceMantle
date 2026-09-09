# Setup status and completion contract (#96)

Status: implemented. This document describes the anonymous Setup entries of the ServiceMantle
management surface. It refines the Setup rows of
[management-entry-authorization.md](management-entry-authorization.md) and adds nothing to them.

## Mapping

`MapServiceMantleSetup` maps both entries at once and requires an explicit consumer transaction
executor:

```csharp
app.MapServiceMantleSetup(async (httpContext, setupCode, cancellationToken) =>
{
    await using var scope = httpContext.RequestServices
        .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
    // validate read-only, orchestrate, stage the consumption, save once, commit
});
```

The entries are direct children of the configured versioned root, mapped beside the protected group
returned by `MapServiceMantleManagementApiV1` and never inside it. With the default root the full
path is `/management/v1/setup`; a custom versioned root moves both. They are mapped at most once. A
missing executor, a missing shared management entry or management API v1 capability, and a second
mapping all fail before the host starts.

## Admission and security baseline

The shared entry baseline admits both entries only in `PendingSetup + Succeeded + Reachable` or
`Completed + Succeeded + Reachable`. `Completed` stays admitted so a replay reaches a stable
conflict instead of a phase rejection. Both entries are anonymous under the Setup rate-limit policy
with the client partition, carry the mandatory security response headers and the correlation
contract, and the `POST` additionally requires exactly one `X-ServiceMantle-Request: 1` header. A
Phase Gate rejection, a rate-limit rejection, and a missing or malformed unsafe-request header all
execute no read, no parser, and no executor.

## Reading the status

`GET` and `HEAD {v1}/setup` read the existing `IServiceInstallationStore` once and project exactly
`{"status":"pending"}` or `{"status":"completed"}`. They expose no Setup Code generation, digest,
issuance or expiry time, operator, contributor, or stored value, and they introduce no second
installation authority. `HEAD` answers the same status code and headers, including `Content-Length`,
with no body. A missing store, an absent installation row, and a store failure all answer
`503 {"errorCode":"management.setup.unavailable"}`.

## Completing the setup

`POST {v1}/setup` reads the installation authority first. An already completed installation answers
the fixed management `409` **without parsing or validating the supplied code at all**, which is what
makes a replay a stable boundary rather than a credential oracle.

While pending, the request must satisfy every rule below, and any violation answers the fixed
management `400` without echoing a byte of the request:

- `application/json`, optionally with a `charset=utf-8` parameter, and nothing else;
- no query string and no `Content-Encoding` header;
- a raw body of at most 4 KiB, counted in bytes before any decoding;
- JSON depth at most 4, no comments, and no trailing commas;
- a top-level object with exactly one property named `code`, matched case sensitively, whose value
  is a string; a repeated `code`, a `Code`, any extra property, and a non-string value are rejected;
- a value that is exactly the existing 32-character, case-sensitive Base64URL Setup Code. It is
  never trimmed or normalized.

The parsed code is then handed to the consumer transaction executor, which owns the shared unit of
work. ServiceMantle owns no wall-clock budget here, because a Setup commit is the consumer's
transaction; an internal timeout owned by the store or the executor maps to the fixed unavailable
result rather than being imposed by the endpoint.

### Required executor sequence

1. Create a fresh asynchronous scope with a clean DbContext and begin its transaction.
2. Call `IServiceSetupCodeStore.ValidateAsync` read-only, before anything is staged.
3. Invoke `ServiceSetupOrchestrator` on that clean staging scope.
4. Call `IServiceSetupCodeStore.StageConsumeAsync`.
5. Call `SaveChangesAsync` exactly once.
6. Commit, and only then report `Committed`.

Any code race after staging, contributor failure, staging, save, commit, cancellation, or cleanup
failure must roll back, discard the whole scope without retrying, and never reuse that DbContext.

## Responses

| Outcome | HTTP result |
| --- | --- |
| Status read | `200` with the fixed status body |
| Commit confirmed | `204` with an empty body and no content type |
| Unusable media, shape, size, depth, code shape, or unsafe header | Fixed management `400` |
| Contributor validation rejected the completion | Fixed management `400` |
| Invalid, malformed, expired, mismatched, never-issued, or replayed code while pending | `401 {"errorCode":"management.setup.credential_invalid"}` |
| Already completed, concurrent completion, or version conflict | Fixed management `409` |
| Store, orchestrator, save, commit, cleanup failure, or internal timeout | `503 {"errorCode":"management.setup.unavailable"}` |
| Caller cancellation | No `IResult`: the original `RequestAborted` token propagates, in preference to any boundary outcome |

The `401` discloses no sub-reason: invalid, malformed, expired, mismatched, never-issued, and
replayed codes are indistinguishable to the caller. A null completion result and an undefined status
value are treated as unavailable, never guessed.

## Cancellation priority

The caller's own cancellation outranks everything a boundary produced. At each of the handlers' three
observation points - the installation store read, the body read and parse, and the executor - the
request's abort is checked before the outcome is classified. If `HttpContext.RequestAborted` is
already cancelled, the handler throws an `OperationCanceledException` carrying exactly that token and
returns no `IResult`, whatever the boundary answered:

| At an observation point, with `RequestAborted` cancelled | Result |
| --- | --- |
| The boundary returned a usable value, a rejection, or null | The caller's cancellation |
| The boundary threw an ordinary failure or an internal timeout | The caller's cancellation |
| The boundary threw a cancellation carrying another token | The caller's cancellation, with the caller's token |

The thrown exception is created by the handler. It is never the exception the boundary raised, so an
internal cancellation is not passed through as if it were the caller's, and its message, inner
exception, the supplied code, and any connection or store detail stay out of it.

When the request was **not** cancelled, nothing changes: an ordinary failure, an internal timeout,
and an internal cancellation carrying a foreign token remain the one fixed
`503 {"errorCode":"management.setup.unavailable"}`, and every other result in the table above keeps
its meaning. A cancellation observed between two phases stops the request there: the parser and the
executor are not entered, and a cancellation observed after the executor returned or threw does not
call it a second time, retry it, or roll anything back on its behalf.

The priority covers these observable boundaries only. It says nothing about a cancellation that
happens after the handler returned, cannot terminate uncooperative synchronous I/O, and imposes no
wall-clock bound. A consumer transaction that already committed is not rolled back by a later
cancellation, no compensation is provided for a malformed or malicious executor, and
`Response.HasStarted` and a failed response write keep following the existing serializer contract.

## Negative disclosure guarantee

Every response body is one of five fixed byte arrays. No candidate Setup Code, digest, generation,
expiry, installation version, contributor value, store error code, or exception text reaches a
serializer, a message, or a ServiceMantle diagnostic. The parser's rented buffer, which held the
candidate, is cleared when it is returned to the pool.

## Explicit non-guarantees

- `ServiceSetupOrchestrator` success and `SetupCodeConsumptionResult.IsStaged` continue to mean
  staged, not committed. Only the executor's `Committed` result states that the shared transaction
  committed, and only the endpoint's `204` reports it.
- ServiceMantle never implicitly commits an existing consumer unit of work. A malformed or malicious
  executor is outside the endpoint guarantee.
- A database rollback cannot restore an external side effect performed by a non-conforming
  contributor; contributors keep their existing staging-only contract.
- Admission does not freeze the phase. A phase change after the Gate admitted the request does not
  recall it; the installation row's own concurrency remains the completion authority.
- The endpoint issues and rotates no Setup Code, provides no cross-request idempotency key, and
  retries no conflict. `Completed` itself is the stable replay boundary.
- `X-ServiceMantle-Request` and JSON-only parsing are a limited browser CSRF mitigation for this
  entry set, not a CORS, TLS, or proxy policy.

# Management session contract (#97)

Status: implemented. This document describes the management identity login, current-session, and
logout entries. It refines the Login, Logout, and Current session rows of
[management-entry-authorization.md](management-entry-authorization.md) and adds nothing to them.

## Mapping

`MapServiceMantleManagementSession` maps all three entries at once and requires an explicit consumer
login adapter:

```csharp
app.MapServiceMantleManagementSession(
    async (httpContext, cancellationToken) =>
    {
        // Parse the media type and schema this service actually supports, inside the 64 KiB
        // envelope ServiceMantle already admitted.
        var credentials = await ReadCredentialsAsync(httpContext, cancellationToken);
        httpContext.RequestServices.GetRequiredService<MyScopedCredentialAccessor>().Set(credentials);
        return await ManagementIdentityProviderInvoker.InvokeAsync(
            httpContext.RequestServices.GetRequiredService<IManagementIdentityProvider>(),
            cancellationToken);
    },
    options => options.LoginTimeout = TimeSpan.FromSeconds(5));
```

The three entries are direct children of the configured versioned root, mapped beside the protected
group returned by `MapServiceMantleManagementApiV1` and never inside it, so the anonymous login
exception exists only in the shared entry baseline and the Admin group keeps rejecting anonymous
access. They are mapped at most once. A missing adapter, a login timeout outside 100 milliseconds
through 30 seconds, a missing capability, and a second mapping all fail before the host starts.

## Admission and security baseline

The shared entry baseline admits all three only in `Completed + Succeeded + Reachable`. Login is
`AllowAnonymous` under the Setup rate-limit policy with the client partition; the current-session
read and the logout require **any** legitimate ServiceMantle management identity cookie - not Admin -
under the management operator-partitioned policy. Both `POST` entries require exactly one
`X-ServiceMantle-Request: 1` header. All three carry the mandatory security response headers and the
correlation contract. A Phase Gate rejection, a rate-limit rejection, a missing or unacceptable
cookie, invalid claims, and a missing unsafe-request header all execute no adapter, no sign-in, and
no sign-out.

## Login

ServiceMantle admits at most 64 KiB of raw request body and rejects a query string or a
`Content-Encoding` header before the adapter runs. The envelope is counted while the body is read,
not taken from a header: a declared length over the limit is the fixed management `400` before a
byte is read, a body with no declared length at all - a chunked upload, for example - meets exactly
the same limit, and a declared length below the limit excuses nothing that follows it. At most one
byte past the envelope is ever read, that byte is what proves the overrun, and the admitted copy is
held in memory and never written to disk.

The host's own request size limit is left exactly as the host set it. ServiceMantle never widens it,
and never narrows it either: a server that counts a chunked request counts its framing too, so
lowering that limit to the envelope would reject a body inside the envelope. A host whose limit is
already smaller still rejects first, and its `413` is reported as the same fixed management `400`.
The media type and the schema inside the envelope stay the adapter's obligation.

The adapter is called with the whole admitted body already in memory. `Request.Body` and a
`Request.BodyReader` obtained inside that call both read the complete original bytes, whether the
adapter reads synchronously, asynchronously, through `CopyToAsync`, or through a `StreamReader`;
they are alternatives rather than a sequence, and interleaving them within one call has no defined
result. An adapter that reads only a prefix, or nothing at all, cannot widen the envelope, because
the decision was already made. When the call ends - returning, throwing, or cancelled - the
request's own body stream and body pipe feature are put back, and only the copy this endpoint
created is released.

The adapter receives the `HttpContext` and a token that is the request token additionally bounded by
the login budget (10 seconds by default). It places credentials only into its own trusted scoped
accessor and calls `ManagementIdentityProviderInvoker`; the public core provider SPI still receives
no credential object. The adapter must not retain or return raw credentials, write to the response,
or sign anything in.

| Login outcome | HTTP result | Cookie effect |
| --- | --- | --- |
| Authenticated and `SignInAsync` completed | `204`, empty body | One fixed-scheme cookie is issued |
| Unauthenticated | `401 {"errorCode":"management.session.unauthenticated"}` | None |
| Failed, null, undefined status, authenticated without an identity | `503 {"errorCode":"management.session.unavailable"}` | None |
| Raw body over the envelope, or the host's own `413` | `400 management.request.invalid` | None; no adapter runs |
| Body read failure, adapter exception, internal cancellation, internal timeout | Same `503` | None |
| Response already started before the sign-in | Same `503` | None; no sign-in is attempted |
| `SignInAsync` failed | Same `503` | Whatever it appended or replaced is rolled back |
| Caller cancellation | The original `RequestAborted` token propagates | None |

### Sign-in rollback

The response's own `Set-Cookie` values are copied before `SignInAsync` starts, and every failure exit
of that sign-in restores the copy before the caller's cancellation is answered. The rollback removes
the complete or chunked ticket the sign-in appended, and undoes a replacement of the whole header,
including the case where the cookie was written by the handler before a `SignedIn` callback threw.
The cookies the response already carried - unrelated ones and an earlier management cookie alike -
come back with their original count, order, and values, and no `SignOutAsync` compensation is used.
The snapshot belongs to the one request that took it. If the snapshot cannot be read, no sign-in is
started at all; if the restore cannot be applied, the connection is aborted rather than completed, so
a response still carrying part of that ticket is never sent and the original exception is not
exposed.

The unauthenticated `401` reuses the existing session error code, status, and content type. It is
written directly instead of through a challenge, so an anonymous login response cannot reveal
whether a cookie happened to be presented. A consumer-supplied provider error code is never copied
into an HTTP response or a ServiceMantle diagnostic: syntactic validity is not proof that a string
is non-secret.

## Current session

`GET` and `HEAD {v1}/session` answer exactly:

```json
{
  "authenticated": true,
  "expiresAtUtc": "2026-09-07T12:34:56.0000000Z",
  "permissions": ["management.read", "management.admin"]
}
```

Permissions are the defined names in the fixed `ManagementPermission` order, independent of claim
order. The projection omits the operator identifier, the display name, the identity source, the
claims, the authentication properties, ticket material, and any provider data. `HEAD` answers the
same status code and headers, including `Content-Length`, with no body. The existing sliding renewal
may still occur, because the read authenticates through the same cookie handler. A principal that no
longer resolves keeps the existing forbidden contract, and a ticket carrying no expiry answers the
fixed unavailable result rather than a guessed value.

Missing, expired, corrupted, and invalid-claim cookies retain the existing `401`/`403` contract
unchanged.

## Logout

`POST {v1}/session/logout` requires a valid identity but not Admin. It calls the fixed scheme's
`SignOutAsync` and answers `204` with no body only once the cookie deletion is on the response. It
deletes this client's own host-scoped cookie and nothing else.

## Explicit non-guarantees

- Logout is local and stateless. It adds no server-side revocation authority and cannot invalidate a
  copied ticket: the same ticket presented from another client still authenticates until it expires.
  Cookie expiry, the Data Protection key ring, and cross-instance semantics are unchanged.
- ServiceMantle implements no concrete account, password, OIDC, or product identity provider, and
  defines no universal credential schema.
- The 64 KiB limit is ServiceMantle's admission envelope only. The consumer adapter must still parse
  its own media type and schema strictly.
- One login budget covers reading the raw body and the adapter call together and is not reset
  between them. A login whose budget is spent while the body is still arriving answers the fixed
  unavailable result and never calls the adapter.
- The login budget bounds waiting on a cooperative adapter and a cooperative body stream. It is not
  a hard wall-clock bound: an adapter, provider, or request stream that ignores its cancellation
  token cannot be forcibly terminated.
- The envelope covers the raw body bytes this endpoint reads and holds. It is not a promise about
  the bytes a client sent on the wire, the number of connections, process memory, the exact size of
  every allocation, or zeroing credential memory. A body an upstream component already consumed, a
  stream or reader a component retained before the handler ran, a pre-parsed form, and same-process
  code that bypasses `Request.Body` and the body pipe feature are outside the adapter's supported
  surface.
- A request the server or a proxy rejects before it reaches the handler, or one whose response has
  already started, is not rewritten into the fixed `400` or `503`.
- ServiceMantle's negative credential guarantee covers its own parsers, fixed responses, projections,
  and diagnostics. It does not cover a consumer accessor, provider, or identity system, third-party
  request logging, or raw request capture.
- The rollback covers `Set-Cookie` values a synchronous or asynchronous sign-in appended or replaced
  in a writable response header collection before it threw. It does not repair a response that has
  already started, undo a ticket copied elsewhere, revoke an external `ITicketStore` side effect, or
  compensate for an adapter, which must not write to the response at all.
- It does not defend against same-process code that bypasses the response headers, modifies the
  response concurrently, or schedules its own callback to issue a ticket after the failure. For a
  header collection that refuses or discards the restore, the fixed `503` is not promised: that
  response is aborted instead.
- Cookies present before the sign-in are kept. A normal sliding renewal of an earlier session that
  was already scheduled is not this login's ticket; the cookie handler suppresses that renewal for a
  request it was asked to sign in.
- `X-ServiceMantle-Request` and the default `SameSite=Strict` cookie are a limited browser CSRF
  mitigation, not a CORS, TLS, origin, or proxy policy.

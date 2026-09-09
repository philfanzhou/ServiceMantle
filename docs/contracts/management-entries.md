# Shared management entry contract

`AddServiceMantleManagementEntries` and `MapServiceMantleManagementEntry` provide the opt-in entry
convention every management entry defined by
[the management entry authorization decision](management-entry-authorization.md) shares: one fixed
path and method per entry kind, a method-aware phase classification, a fixed anonymous or policy
authentication rule, a fixed named rate-limit policy, the mandatory security response headers, and
the unsafe-request header guard.

The convention maps no business handler. The consuming service supplies the handler behind each
entry; the status, Bootstrap, Setup, and session behaviors themselves are owned by their own issues.

## Minimal wiring

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddManagementCookieAuthentication()
    .AddServiceMantleManagementApiV1()
    .AddServiceMantleManagementEntries();

builder.Services.AddSingleton<IServiceHealthSnapshotSource, ProductSnapshotSource>();

var app = builder.Build();
app.UseServiceMantlePipeline();

app.MapServiceMantleManagementEntry(
    ServiceMantleManagementEntryKind.InstallationStatus,
    ReadInstallationStatus);
app.MapServiceMantleManagementEntry(
    ServiceMantleManagementEntryKind.SessionLogin,
    SignIn);
```

Entries are mapped on the application, beside the protected group returned by
`MapServiceMantleManagementApiV1`, never inside it. They reuse that entry point's configured
versioned root, so `{v1}` below is whatever `AddServiceMantleManagementApiV1` normalized. A host maps
only the entries it actually serves; an unmapped kind exposes nothing.

## The entry table

| Kind | Method and path | Admitted phase | Authentication | Rate limit |
| --- | --- | --- | --- | --- |
| `InstallationStatus` | `GET`, `HEAD {v1}/status` | Every phase; the gate reads no snapshot | Anonymous | Management policy, anonymous client partition |
| `BootstrapCreate` | `POST {v1}/bootstrap` | `BootstrapConfiguration`, migration `NotStarted` or `Succeeded` | Anonymous | Setup policy, client partition |
| `BootstrapUpdate` | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementAdmin` **and** `ServiceMantle.ManagementSession`, so the fixed management cookie scheme | Management policy, operator partition |
| `SetupStatus` | `GET`, `HEAD {v1}/setup` | `PendingSetup` or `Completed`, with `Succeeded + Reachable` | Anonymous | Setup policy, client partition |
| `SetupComplete` | `POST {v1}/setup` | Same as `SetupStatus`, so a completed replay reaches the handler | Anonymous | Setup policy, client partition |
| `SessionLogin` | `POST {v1}/session/login` | `Completed + Succeeded + Reachable` | Anonymous | Setup policy, client partition |
| `SessionLogout` | `POST {v1}/session/logout` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementSession` | Management policy, operator partition |
| `CurrentSession` | `GET`, `HEAD {v1}/session` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementSession` | Management policy, operator partition |

Reserved branch matching uses the existing segment-aware, case-insensitive phase gate rules, so a
look-alike such as `{v1}/bootstrap-other` is not a Bootstrap entry and is not routed at all. A method
the table does not define never reaches a handler: it is stopped by the existing gate rule for an
unclassified endpoint inside the management namespace, which answers
`503 {"errorCode":"service.phase.unavailable"}`.

Because the gate runs before authentication, rate limiting, and authorization, a wrong phase is
answered without inspecting a credential or cookie. Every admitted entry other than
`InstallationStatus` observes exactly one gate snapshot. A phase change after admission does not
recall a request that already entered its handler; handlers keep their own concurrency authority.

## Session authorization

`ServiceMantle.ManagementSession` is stricter than `RequireAuthenticatedUser` and weaker than
`ServiceMantle.ManagementAdmin`: it pins the fixed management cookie scheme and requires the
principal's ServiceMantle claims to resolve to exactly one legitimate operator, but requires no
permission. An authenticated principal without acceptable ServiceMantle claims is rejected. The
existing cookie results are unchanged — `401 management.session.unauthenticated` with no cookie,
`401 management.session.expired` for a presented but unacceptable cookie, and
`403 management.session.forbidden` for a valid identity that lacks the required permission or carries
invalid claims. The policy applies to the opt-in entries only; the protected group's general support
for a consumer-supplied authentication scheme is unchanged.

`InstallationStatus` uses the management rate-limit policy but is pinned to the anonymous client
partition, so presenting a valid management cookie cannot move it onto a per-operator quota. Every
other management entry keeps the existing per-operator partition.

### The Bootstrap update pins the scheme

Rewriting the Bootstrap file of a running instance is a local administrator action, so its
authorization conclusion must come from this host's own management cookie and not from whatever
default scheme a consuming service happens to have configured. `ServiceMantle.ManagementAdmin` is
deliberately authentication-method agnostic and cannot express that on its own, so `BootstrapUpdate`
is authorized with both policies. Combining them unions the schemes, and only the session policy
names one, so the entry's effective scheme set is exactly the fixed management cookie scheme, while
its requirements are the session policy's legitimate current operator plus the administrator
permission.

Mapping `BootstrapUpdate` therefore requires the fixed management cookie scheme to be registered.
Its cookie results are the ones listed above: no cookie is `401 management.session.unauthenticated`,
an unacceptable cookie `401 management.session.expired`, and a legitimate identity without the
administrator permission `403 management.session.forbidden` - an external administrator with no
cookie among them. No other entry and no other policy changes: the general administrator policy and
the ordinary protected `MapServiceMantleManagementApiV1` group still accept a consuming service's
own authentication scheme, and a host that does not map `BootstrapUpdate` gains no cookie
prerequisite.

## Unsafe requests

Every unsafe entry method requires exactly one `X-ServiceMantle-Request: 1` header. A missing, empty,
repeated, comma-combined, or different value produces the fixed management invalid-request response
(`400 application/problem+json` with `management.request.invalid`) before the handler runs, and the
response carries no request value. `GET` and `HEAD` entries do not carry the guard and it produces no
side effect on them.

The header, JSON-only parsing in the entry handlers, and the default `SameSite=Strict` management
cookie together form a limited browser CSRF mitigation for this entry set: they prevent a simple
cross-site HTML form from issuing a conforming request. They are not a global CSRF policy and do not
replace a correct CORS policy, trusted-origin validation, TLS, or proxy configuration, and they do
not protect against compromised same-origin script. A consumer that loosens `SameSite` or allows
cross-origin custom headers owns the resulting policy.

## Startup validation

With at least one entry mapped, the host fails to start when:

- the same entry kind is mapped more than once;
- an entry's path, method set, or phase-gate surface does not match its fixed definition;
- an entry is mapped through the protected `MapServiceMantleManagementApiV1` group, or carries a
  second management surface;
- a convention downgrades the baseline: `AllowAnonymous` on a protected entry, a missing
  `AllowAnonymous` on an anonymous one, `DisableRateLimiting`, a replaced rate-limit policy, missing
  security response headers, or a missing unsafe-request guard;
- the composed ServiceMantle pipeline did not run on the builder the entries were mapped on;
- the security response headers, the named rate-limit policies, or the phase gate are not registered;
- a protected entry's authorization policy or the default authenticate, challenge, and forbid schemes
  cannot be resolved, or an entry that resolves through `ServiceMantle.ManagementSession` - the
  session entries and `BootstrapUpdate` - is mapped without the fixed management cookie scheme;
- the effective authentication scheme set of `BootstrapUpdate` is not exactly the fixed management
  cookie scheme. The set is read from the endpoint's combined authorization data, so a scheme named
  on the endpoint and a further policy that names one are both refused. A further policy that only
  adds requirements names no scheme, leaves the set unchanged, and is admitted as a stricter rule
  rather than a downgrade.

`MapServiceMantleManagementEntry` itself rejects an undefined kind and a host that has not registered
`AddServiceMantleManagementApiV1` and `AddServiceMantleManagementEntries`. Startup failures name no
operator, claim, credential, or configuration value.

## Explicit non-guarantees

- The convention implements no status projection, Bootstrap file, credential, Setup transaction,
  identity provider call, or cookie issue and clearing. Those belong to the entry issues.
- The fixed header is not a global CSRF, CORS, TLS, proxy, firewall, or DDoS policy.
- A gate snapshot is one observation at the start of a request, not a lock over later phase changes.
- Runtime route reconfiguration is outside startup validation; the per-request checks still reject a
  mismatched surface or method conservatively.
- The scheme pinning covers the static endpoint and policy composition of a normally started host
  only. It does not defend against a host that replaces the implementation behind the fixed scheme,
  a hostile custom policy provider, route or policy changes made after startup, or in-process
  tampering, and it changes nothing about the cookie's trust root, expiry, revocation, or
  cross-instance behaviour.

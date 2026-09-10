# Reference service skeleton

This is a consumer-owned acceptance host, not a production template. It deliberately exposes only
`GET /`, which returns `status: skeleton`. By default startup does not create a database, execute
migrations, run setup contributors, provision administrators, or enable management/health/telemetry
endpoints. One opt-in startup deployment gate can be switched on explicitly; it is off unless every
input below is stated.

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- --urls http://127.0.0.1:5080
```

The example uses the same public ServiceMantle package projects as an external consumer. Repository
builds use `ProjectReference` to those packable projects; they never import library source files or
use library internals/`InternalsVisibleTo`. Published-package consumption is a separate release
acceptance task (#113). The sample and its smoke tests are included in the solution; the test project
is registered under ASP.NET Core in `eng/packages.json`, so the existing ReleaseTool and CI restore,
build and test both projects without a separate sample list.

| Consumer-owned component | Current boundary | Follow-up |
| --- | --- | --- |
| `ReferenceApplication` | Public composition seam, one skeleton route | Shared by integration tasks |
| `ReferenceDbContext` and `Data/Migrations` | One workspace table; caller owns migration, save and transaction | #160 |
| `Database/Sqlite/` | Opt-in SQLite startup deployment gate and the consumer-owned migration executor | #112 / #113 |
| `ReferenceSetupContributor` | Read-only validation and staging-only example; never invoked at startup | [#175](https://github.com/philfanzhou/ServiceMantle/issues/175) |
| `ReferenceSettingDefinitions` | Defaults and constraints only; no store, HTTP or activation | #177 |
| `ReferenceReadinessContributor` | Returns `reference.health_not_integrated`; never claims readiness | #156 |
| `ExternalManagementIdentityPlaceholder` | Returns Failed with a safe unconfigured-provider code | Future external identity integration |
| `Logging/` | Opt-in Serilog Console wiring and one sanitized request line | Delivered by [#157](https://github.com/philfanzhou/ServiceMantle/issues/157) / [PR #307](https://github.com/philfanzhou/ServiceMantle/pull/307) |
| `Telemetry/` | Opt-in base ASP.NET Core, HttpClient and runtime instrumentation; no exporter | [#158](https://github.com/philfanzhou/ServiceMantle/issues/158) |

EF SQLite is the consumer's model carrier. With the startup gate off, the ServiceMantle SQLite
providers are not registered at all: the default file is `reference.db` below the content root,
`ReferenceService:DatabasePath` can change it, and merely starting the host neither creates the file
nor migrates it. The initial migration and snapshot live here, never in ServiceMantle.

## Explicit SQLite startup deployment

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:SqliteStartup:Enabled true \
  --ReferenceService:SqliteStartup:DeploymentMode SingleInstance \
  --ReferenceService:SqliteStartup:PrepareIfMissing true \
  --ReferenceService:DatabasePath /absolute/path/reference.db \
  --urls http://127.0.0.1:5080
```

`ReferenceService:SqliteStartup:Enabled` defaults to `false`; only an explicit `true` activates the
gate. When it is on:

- `DeploymentMode` must be stated and must be `SingleInstance`. `Unspecified`, `MultiInstance`, and
  any unparsable value are refused. Being the only process on the machine, holding no lock, or
  omitting the setting is never read as authorization.
- `ReferenceService:DatabasePath` must be an absolute local ordinary-file path.
- `ReferenceService:SqliteStartup:PrepareIfMissing` defaults to `false`. Only an explicit `true`
  lets a missing file be created; an unparsable value is refused.

An unusable input fails before any provider, file, or EF call, and the failure names the setting, not
the value. The preparation call and the wait for the process-local single-instance turn share one
fixed 5-second budget; nothing bounds the migration itself or total startup. The sample builds its
own SQLite connection - non-pooled, private cache, no administrative connection or password input -
and EF uses `Mode=ReadWrite`, so EF's default `ReadWriteCreate` can never create the file behind the
gate's back.

The gate then runs in a fixed order and fails closed at every step: the deployment mode is validated
from the captured capability declarations first; a read-only observation decides whether the target
is there; a missing target stops the startup unless preparation was explicitly permitted; and the
consumer's own scoped executor inspects, migrates at most once, and inspects again. Only a
successful migration lets the host finish starting - the gate runs before any hosted service, so no
request is ever served over an unmigrated database. A failure closes startup and logs one fixed
outcome, with no path, connection string, provider message, or exception text.

The inspection is deliberately conservative. An empty readable database is adoptable; the complete
known migration set with a readable workspace table is current; a strict prefix is pending; an
unknown history record is treated as a newer schema; and application tables without a history, a
missing required table, or an unreadable history all refuse the database rather than adopting or
repairing it. The inspection opens its own read-only connection and never calls `Migrate`,
`EnsureCreated`, or `SaveChanges`.

There is no cross-process or cross-host exclusion: two processes that both declare `SingleInstance`
are a deployment error this contract cannot detect. Nothing spans the file publication and the
migration transactionally - a committed migration or a published empty file is not undone by a later
cancellation, and no interrupted database is repaired automatically. The sample is a consumer
example; a production service chooses its own migration strategy.

The staging example creates a new workspace on each explicit `RegisterAsync` call, with a generated
ID and a fixed demo display name. It is not an installation workflow or an idempotency contract.
It requires a single caller-owned scoped context. Only the caller can save or commit those staged
changes; the smoke tests apply the migration explicitly and demonstrate this boundary, including
rollback and cancellation before staging. They do not invoke full setup orchestration.

The identity placeholder does not invent an unauthenticated-success story for an unavailable
external system, emit credentials, contact a network service, or create a local administrator.
No local-administrator entity or provisioning path exists in this sample.

Configuration management transaction audit remains in
[#177](https://github.com/philfanzhou/ServiceMantle/issues/177), first-install transaction audit in
[#175](https://github.com/philfanzhou/ServiceMantle/issues/175), telemetry in
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158) and Consul in
[#159](https://github.com/philfanzhou/ServiceMantle/issues/159). This skeleton makes no guarantees
about TLS, deployment, reverse proxies, production security hardening, API compatibility, final
management routes or multi-instance E2E behavior. Its startup status is not a health/readiness
claim.

## Logging safety wiring

`ReferenceService:Logging:Enabled` is an explicit boolean switch that defaults to `false`. A missing
or unparsable value leaves the ServiceMantle Serilog host, the sensitive Header registry, and the
request log unregistered; the value is fixed before `Build` and is never reloaded. With the switch
off the host still starts, serves `GET /`, stops and disposes normally.

```bash
dotnet run --project samples/ServiceMantle.ReferenceService --   --ReferenceService:Logging:Enabled true --urls http://127.0.0.1:5080
```

When it is on, the sample registers the existing `ServiceMantle.Serilog` Console pipeline with its
mandatory structured sanitization, adds the sample-owned `X-Reference-Secret` to the built-in denied
Header names through `AddSensitiveHeaders`, and composes Correlation ID outside Problem Details so
one identifier enriches the whole downstream scope. Its single request line carries only a fixed
message template, a bounded result classification (`success`, `client_error`, `server_error`,
`other`, `cancelled`, `faulted`), the status code, the request method collapsed to the framework's
known token set (anything else becomes `(other)`), the matched route pattern, and the Header graph
produced by the DI-owned `RequestHeaderDiagnosticProjector`. No raw path, query, body,
connection setting, or exception detail is logged, and no second sanitizer is registered.

Header values follow the library contract rather than an allow list: the built-in denied Headers and
the sample-owned `X-Reference-Secret` are replaced in full by the redaction marker, while the values
of Headers outside the denied list — `User-Agent`, `Referer`, `X-Forwarded-For`, and any caller
Header the sample never declared — are projected under the free-text rules of
[the structured logging security contract](../../LOGGING_SECURITY.md) and therefore do reach the log
line. Only the shapes that contract recognizes are redacted there. Add a Header name through
`AddSensitiveHeaders` to keep its values out. Caller cancellation stays cancellation and is never
swallowed. The phase gate, health endpoints, management routes, and rate limiting stay unwired here;
base telemetry has a switch of its own, described below, and neither switch changes the other.

The safety boundary is the one documented in [the structured logging security
contract](../../LOGGING_SECURITY.md). Denied structured field names, denied Headers, supported
sensitive value types, and the explicitly recognized free-text shapes are redacted; unlabelled opaque
secrets, values that never pass through the sanitizer or projector, third-party providers, arbitrary
framework events, and remote systems are not covered. Enabling logging does not create the database,
run migrations, invoke setup, or save a consumer `DbContext`. The failure, rejection, and exception
routes used by the tests are mapped by the tests on the public `Build` seam; the running sample keeps
only the skeleton route.

## Base telemetry instrumentation

`ReferenceService:Telemetry:Enabled` is an explicit boolean switch that defaults to `false`. Only a
value that parses to `true` registers instrumentation; a missing, empty, or unparsable value leaves
every ServiceMantle-owned OpenTelemetry provider unregistered. The value is read before the host
builder is created, is fixed before `Build`, and is never reloaded.

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:Telemetry:Enabled true --urls http://127.0.0.1:5080
```

When it is on, the sample calls `AddOpenTelemetryInstrumentation` and nothing else. That is the fixed
set the public `ServiceMantle.OpenTelemetry` package already ships: ASP.NET Core request tracing,
`HttpClient` tracing, and .NET runtime metrics. The sample adds no options system of its own on top
of it. The OpenTelemetry resource is exactly the identity `AddServiceMantle` already registered — the
service name, the service version, and the instance ID — so the sample contributes no attribute, no
high-cardinality dimension, and reads no Header, body, query, or connection field.

What this switch does **not** do: it wires no OTLP exporter, no Prometheus endpoint, no
`ServiceMetrics`, no health endpoint, and no service or installation phase metric, and it
fabricates no phase. `/metrics`, `/health`, and `/management` still return 404 with the switch on.
Nothing here creates a remote export target, so with no exporter registered the collected signals
have nowhere to go. Those capabilities stay with
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158) and the tasks that own them.

The sample now takes a **static** dependency on `ServiceMantle.OpenTelemetry` and its instrumentation
packages: they are in the build output whether or not the switch is on. Turning the switch off stops
the ServiceMantle-owned providers and listeners from being registered; it does not remove those
assemblies from the published output, and it is not a claim that the .NET process runs no thread,
timer, or socket of any kind.

See [`docs/testing/reference-telemetry.md`](../../docs/testing/reference-telemetry.md) for the
acceptance matrix and the full list of things this wiring does not guarantee.

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release
```

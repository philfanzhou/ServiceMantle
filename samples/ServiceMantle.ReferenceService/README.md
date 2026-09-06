# Reference service skeleton

This is a consumer-owned acceptance host, not a production template. It deliberately exposes only
`GET /`, which returns `status: skeleton`. Startup does not create a database, execute migrations,
run setup contributors, provision administrators, or enable management/health/telemetry endpoints.

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
| `ReferenceSetupContributor` | Read-only validation and staging-only example; never invoked at startup | #160 |
| `ReferenceSettingDefinitions` | Defaults and constraints only; no store, HTTP or activation | #177 |
| `ReferenceReadinessContributor` | Returns `reference.health_not_integrated`; never claims readiness | #156 |
| `ExternalManagementIdentityPlaceholder` | Returns Failed with a safe unconfigured-provider code | Future external identity integration |
| `Logging/` | Opt-in Serilog Console wiring and one sanitized request line | #175 |

EF SQLite is configured solely as a consumer model carrier. The optional ServiceMantle SQLite
installation provider is not registered. The default file is `reference.db` below the content root;
`ReferenceService:DatabasePath` can change it. Merely starting the host does not create the file.
The consumer's future maintenance/startup path must explicitly invoke `Database.MigrateAsync` and
own its error handling. The initial migration and snapshot live here, never in ServiceMantle.

The staging example creates a new workspace on each explicit `RegisterAsync` call, with a generated
ID and a fixed demo display name. It is not an installation workflow or an idempotency contract.
It requires a single caller-owned scoped context. Only the caller can save or commit those staged
changes; the smoke tests apply the migration explicitly and demonstrate this boundary, including
rollback and cancellation before staging. They do not invoke full setup orchestration.

The identity placeholder does not invent an unauthenticated-success story for an unavailable
external system, emit credentials, contact a network service, or create a local administrator.
No local-administrator entity or provisioning path exists in this sample.

Audit, telemetry and Consul integration remain in #175, #158 and #159. This
skeleton makes no guarantees about TLS, deployment, reverse proxies, production security hardening,
API compatibility, final management routes or multi-instance E2E behavior. Its startup status is not
a health/readiness claim.

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
produced by the DI-owned `ServiceMantleRequestHeaderDiagnosticProjector`. No raw path, query, body,
connection setting, or exception detail is logged, and no second sanitizer is registered.

Header values follow the library contract rather than an allow list: the built-in denied Headers and
the sample-owned `X-Reference-Secret` are replaced in full by the redaction marker, while the values
of Headers outside the denied list — `User-Agent`, `Referer`, `X-Forwarded-For`, and any caller
Header the sample never declared — are projected under the free-text rules of
[the structured logging security contract](../../LOGGING_SECURITY.md) and therefore do reach the log
line. Only the shapes that contract recognizes are redacted there. Add a Header name through
`AddSensitiveHeaders` to keep its values out. Caller cancellation stays cancellation and is never
swallowed. The phase gate, health endpoints, management routes, telemetry, and rate limiting stay
unwired here.

The safety boundary is the one documented in [the structured logging security
contract](../../LOGGING_SECURITY.md). Denied structured field names, denied Headers, supported
sensitive value types, and the explicitly recognized free-text shapes are redacted; unlabelled opaque
secrets, values that never pass through the sanitizer or projector, third-party providers, arbitrary
framework events, and remote systems are not covered. Enabling logging does not create the database,
run migrations, invoke setup, or save a consumer `DbContext`. The failure, rejection, and exception
routes used by the tests are mapped by the tests on the public `Build` seam; the running sample keeps
only the skeleton route.

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release
```

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

Audit, logging safety, telemetry and Consul integration remain in #175, #157, #158 and #159. This
skeleton makes no guarantees about TLS, deployment, reverse proxies, production security hardening,
API compatibility, final management routes or multi-instance E2E behavior. Its startup status is not
a health/readiness claim.

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release
```

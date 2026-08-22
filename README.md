# ServiceMantle

ServiceMantle is a shared .NET 10 library for reusable service-management foundations used by ASP.NET Core services.

Current status: **early development**. Service identity and installation phase primitives are implemented; database, management authentication, auditing, observability, and service discovery capabilities are not yet implemented.

`ServiceId` is a stable deployment-level identifier shared by all instances of one service. `InstanceId` identifies one running instance for runtime diagnostics and must not be used as a substitute for `ServiceId`.

## Phase 1 scope

- bootstrap
- installation
- configuration
- administration abstraction
- auditing
- observability
- discovery

## Non-goals (first version)

- No product-specific user / OAuth / JWT domain models.
- No management frontend.
- No business migration or service-specific logic.

## Frontend note

Frontend work is intentionally out of scope and will be implemented in a separate `ServiceMantle.Console` project.

## Repository layout

- `src/ServiceMantle/ServiceMantle.csproj`
- `tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj`
- `src/ServiceMantle/ServiceId.cs`
- `src/ServiceMantle/InstanceId.cs`
- `src/ServiceMantle/Installation/`
- `tests/ServiceMantle.Tests/ServiceIdTests.cs`
- `tests/ServiceMantle.Tests/InstanceIdTests.cs`
- `tests/ServiceMantle.Tests/Installation/`
- `ServiceMantle.slnx`
- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props`

## Local build commands

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack src/ServiceMantle/ServiceMantle.csproj -c Release --no-build
```

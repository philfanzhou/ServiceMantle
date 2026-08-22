# ServiceMantle

ServiceMantle is an early-stage .NET 10 shared backend library that will host common service-management foundation capabilities for ASP.NET services.

Current status: **early development**. The repository currently contains only a minimal, testable skeleton and does not include the real business logic yet.

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
- `src/ServiceMantle/AssemblyMarker.cs`
- `tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj`
- `tests/ServiceMantle.Tests/SmokeTests.cs`
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

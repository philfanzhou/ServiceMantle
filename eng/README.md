# Package pipeline

`packages.json` is the single registration source for every package shipped by this repository. Each entry declares:

- the package ID and project path;
- whether the package is optional;
- every direct NuGet/project dependency and shared-framework reference;
- one or more test projects and any environment variables needed by their integration tests.

CI and Release call `ServiceMantle.ReleaseTool`; they do not contain per-package build, test, or pack steps. To add an optional package, create its package and test projects, then add one entry to `packages.json`. No workflow structure change is required. The registry validator fails when paths, IDs, dependencies, framework references, test ownership, or environment declarations disagree with the projects.

For a test project that requires a real database, set `realDatabase` to `true`, register its
`RUN_SERVICEMANTLE_*_TESTS=true` environment variable, and classify every real-database fixture
with `RealDatabaseTestAttribute` from `tests/ServiceMantle.Testing`. The release tool first proves
that at least one `Category=RealDatabase` test is discoverable, then runs the project with skipped
tests treated as failures. Local runs may leave the requirement variable unset and skip the fixture;
once the registry marks the environment as required, an unavailable service cannot silently pass CI.
The same test-support project provides the fixed credential-injection contract and the bounded,
in-process `TwoActorBarrier` used by provider concurrency fixtures.

SQL Server real-database registrations also declare their Docker daemon requirements in
`packages.json`. Before the first such test project starts, the release tool queries the actual
daemon once and requires `OSType=linux`, an `amd64`/`x86_64` architecture, and at least
`2147483648` bytes of memory. Later SQL Server projects reuse that immutable result. A missing or
unreachable daemon, malformed output, or an unsupported daemon fails the test stage before any SQL
Server test process or container starts; diagnostics include only the observed OS, architecture,
and total memory.

On Apple Silicon, connect Docker to a Linux x86-64 VM or remote daemon with at least 2 GiB of
memory. Running the SQL Server Linux image through QEMU or another architecture translation layer
is outside the supported path; the client machine and its .NET process may remain arm64 because the
preflight evaluates the daemon rather than the client.

The local equivalents of the workflow stages are:

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- validate
dotnet run --project eng/ServiceMantle.ReleaseTool -- restore
dotnet run --project eng/ServiceMantle.ReleaseTool -- build --version 0.0.0-local.1 --commit local
dotnet run --project eng/ServiceMantle.ReleaseTool -- test
dotnet run --project eng/ServiceMantle.ReleaseTool -- pack --version 0.0.0-local.1 --commit local --output artifacts/packages
dotnet run --project eng/ServiceMantle.ReleaseTool -- verify --version 0.0.0-local.1 --commit local --input artifacts/packages
```

`verify` requires exactly one `.nupkg` and one `.snupkg` per registration. It validates IDs, versions, MIT license, repository URL/commit, framework references, the complete dependency set, and same-version references between ServiceMantle packages before artifacts are uploaded.

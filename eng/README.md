# Package pipeline

`packages.json` is the single registration source for every package shipped by this repository. Each entry declares:

- the package ID and project path;
- whether the package is optional;
- every direct NuGet/project dependency and shared-framework reference;
- one or more test projects and any environment variables needed by their integration tests.

CI and Release call `ServiceMantle.ReleaseTool`; they do not contain per-package build, test, or pack steps. To add an optional package, create its package and test projects, then add one entry to `packages.json`. No workflow structure change is required. The registry validator fails when paths, IDs, dependencies, framework references, test ownership, or environment declarations disagree with the projects.

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

# Reference service SQLite deployment acceptance

`ReferenceSqliteDeploymentEndToEndTests` accepts the reference sample's explicit single-instance
SQLite deployment from outside the process. It is the only coverage in this repository that starts
the sample's real entry point as a separate operating-system process, so it is the only coverage
that can speak about process exit codes, a real Kestrel binding, and the files a real start and stop
leave behind.

## How it runs

The tests live in the existing `tests/ServiceMantle.ReferenceService.Tests` project and run with
every other test in it. They need no container, no environment variable, and no network beyond
loopback, and they are never skipped.

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release --no-restore
dotnet test --solution ServiceMantle.slnx -c Release --no-build --no-restore
```

To run this acceptance alone:

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release \
  --filter-class "ServiceMantle.ReferenceService.Tests.ReferenceSqliteDeploymentEndToEndTests"
```

The sample is not rebuilt or republished by the tests. `ReferenceServiceBuildOutput` resolves the
sample's build output as the sibling of the test assembly's own output directory under the same
configuration, and a start fails with an explicit message when that output is missing. Build the
solution first.

Each case starts the sample's own executable with `--urls=http://127.0.0.1:0`, so the operating
system chooses the port and Kestrel reports the address it bound on the host's own console output.
`ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` are fixed to `Production` so the environment of
the machine running the tests never decides the deployment path.

## The matrix

| Case | Explicit inputs | Evidence |
| --- | --- | --- |
| Gate off by default | database path only | `GET /` returns 200; no file, not even an empty one, appears in the directory |
| First authorized start | `Enabled`, `SingleInstance`, `PrepareIfMissing=true`, missing target | the workspace migration is applied before Kestrel reports its address; history holds exactly the one known migration; the workspace table is empty |
| Restart over the same target | same, `PrepareIfMissing=false` | serves again; history does not grow; a row written between the two runs is unchanged; the file is byte-identical |
| Unauthorized deployment mode | undeclared, empty, `Unspecified`, `MultiInstance`, `3`, `not-a-mode`, each over a missing and over an existing target | non-zero exit; the host never listens; the failure names the setting and not its value; no database or sidecar is created and an existing file is byte-identical |
| Missing target, preparation not authorized | `PrepareIfMissing=false`, missing target | non-zero exit; the finite outcome is `TargetMissing`; nothing is created |
| Unusable existing target | a file that is not a database | non-zero exit; a finite outcome; the file is byte-identical - it is neither adopted, repaired, nor replaced |
| Unknown migration history | a record this build does not know, written by the fixture into its own stopped database | non-zero exit; the finite outcome is `MigrationFailed`; both history records are still there and the file is byte-identical |
| Stopping a running host | authorized start | the process exits, the target can then be taken exclusively and deleted, and no sidecar is left |
| Output safety | authorized start | neither the captured console output nor the HTTP body carries the target path, `Data Source`, or the connection string's mode |
| Build output | - | the sample's declared assemblies are present, and no other database provider is on the path |

## Ownership and cleanup

Every case owns its own temporary directory under the system temporary path, resolved through any
symbolic link, because the SQLite file target contract refuses a linked path. The directory is also
the child process's working directory, so a file the sample would create from a relative default
would land where the test inspects. The helper reclaims the process on dispose, including a process
tree, and the directory is removed with it.

The timeouts in `ReferenceServiceBudgets` are this acceptance's cleanup budgets: they bound how long
a test waits before it reports a failure and takes the process back. They are not a statement about
how long the reference service is allowed to take to start or stop in a deployment.

## What this acceptance does not claim

- **No cross-process exclusion.** Two processes that both declare `SingleInstance` are a deployment
  error the sample's contract cannot detect, and nothing here detects one either.
- **A completed start is a migrated schema, not a completed setup.** No service setup, initial
  configuration, installation status, or readiness is exercised or claimed; `GET /` is still the
  skeleton route.
- **No cross-step atomicity.** File publication and the EF migration are separate steps. A published
  file or a committed migration is not undone by a later cancellation, and no arbitrary termination
  point, external file replacement, linked or network file system, or power loss is covered.
- **Bounded output claims.** The absence of a path or a connection string is asserted over what this
  test captures: the sample's own start classification, its exception messages, and its HTTP output.
  Nothing is claimed about arbitrary third-party or framework logs, process memory, operating-system
  diagnostics, or raw database contents. The sample's path takes no password or administrative
  connection input, so no secret protocol is invented to test one.
- **Graceful shutdown is signalled where the platform allows it.** On POSIX platforms the host is
  asked to stop with `SIGTERM` and the exit code is asserted to be zero. Windows offers no equivalent
  request to a child process started this way, so there the process is reclaimed forcibly and only
  its termination and the release of the target are asserted.

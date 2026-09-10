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

Every registered test project has a 10-minute Microsoft.Testing.Platform global timeout. The
real-database discovery preflight has a separate 2-minute timeout. Each invocation enables
synchronous MTP diagnostic logging under
`artifacts/test-diagnostics/<repository-relative-project-path>/test` or `list-tests`, so a runner
that hangs during execution or process exit fails before the CI job timeout while retaining the
diagnostic output already written. CI uploads that directory when the test job fails or is
cancelled; a run that fails before creating diagnostics does not turn the upload step into another
failure. Download the `test-diagnostics-<run-id>-<attempt>` artifact from the workflow run to inspect
the `.diag` files. The directory and artifact names are derived only from registered project paths
and GitHub run metadata, never from registered test environment values.

Registered test invocations run inside a dedicated POSIX process group or Windows job. A small
ReleaseTool host establishes that scope before starting `dotnet`, reports the original runner exit
code, and stays alive until ReleaseTool terminates the scope. This also removes ordinary descendants
whose immediate parent has already exited, on success, internal MTP timeout, or caller cancellation.
Internal timeout remains a pipeline failure; caller cancellation remains exit code 130. Host startup
has a separate 30-second protocol deadline and fails before starting tests if isolation cannot be
established. Test standard output/error and synchronous diagnostic files retain their existing paths.
This is process lifetime management, not a sandbox: cleanup does not cover processes deliberately
leaving the POSIX group, processes launched through external services, or Docker containers. It does
not promise graceful child shutdown or cleanup after ReleaseTool itself is forcibly terminated.

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

## Release versions

`resolve-version` is the only place a release version is decided, so a workflow never repeats the
rule:

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- resolve-version \
  --ref-name "$GITHUB_REF_NAME" \
  --tagged true \
  --untagged-version "0.0.0-edge.$GITHUB_RUN_NUMBER.$GITHUB_RUN_ATTEMPT"
```

It prints `number=` and `publish=` on separate lines, ready to append to `$GITHUB_OUTPUT`. A tag
publishes the version it names with the leading `v` removed; any other ref produces the untagged
version and `publish=false`.

Both paths are held to the same rule: the version has to parse as a NuGet version, carry no build
metadata, and already be in NuGet's normalized form. `v1.2`, `v01.0.0`, and `v1.0.0.0` are rejected
rather than quietly published as `1.2.0` or `1.0.0`, because a version NuGet rewrites is a version
that no longer matches the tag a consumer was told to pin. Build metadata is rejected because NuGet
drops it, which would let `v1.0.0+a` and `v1.0.0+b` collide on one package slot.

## Publishing

`publish` pushes the registered set to a NuGet v3 feed:

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- publish \
  --version 0.1.0-rc.1 --commit "$GITHUB_SHA" \
  --input artifacts/packages \
  --source https://api.nuget.org/v3/index.json \
  --api-key-environment SERVICEMANTLE_NUGET_API_KEY
```

It runs the same `verify` checks first, so an incomplete or mislabelled artifact set fails while
nothing is public yet. Then, per package: if the feed already has that ID and version, the published
package's `repository/@commit` is compared against `--commit`. Equal means an earlier run of this
same release already pushed it, and it is skipped as `already present`; different means someone else
owns that version, and the package fails. Nothing is ever overwritten. Add `--dry-run` to run every
check and every feed comparison without pushing.

A multi-package push is not a transaction, and this command does not pretend otherwise. An
interruption can leave the feed holding part of the set; rerunning the same version finishes the
rest, because the packages already there take the `already present` path. Every package is attempted
even after one fails, so the closing summary reports the complete state of the feed - published,
already present, and failed, each with the package IDs and versions - rather than stopping at the
first problem. Any failure, including a rejected credential, exits non-zero.

A feed that stops answering is one of those failures, not an interruption. Each request has a
five-minute limit, and a request that outlives it is recorded against its own package and counted as
failed, so the summary still prints and the command exits 1. Exit code 130 stays reserved for the
caller actually cancelling the run.

The credential is read from the environment variable named by `--api-key-environment` and travels to
the feed in an `X-NuGet-ApiKey` header, so it never reaches a child process's argument list. Every
line the command prints passes through a redactor keyed on that value, which covers diagnostics
assembled from feed responses this tool does not author. The redactor scans each write as a whole,
so a diagnostic printed in one call is covered; it does not buffer across separate writes.

# Releasing ServiceMantle

Every package in `eng/packages.json` is published together, at one version, from one tag. There is
no per-package release and no manual push.

## Cutting a release

1. Make sure the commit you want to release is already on `main`. A tag anywhere else is refused.
2. Tag that commit and push the tag:

   ```bash
   git tag v0.1.0-rc.1 <commit-on-main>
   git push origin v0.1.0-rc.1
   ```

3. Watch the **Package release** workflow. On success every registered package and its symbol
   package are on NuGet.org at the tagged version, and a consumer project has restored that version
   back from NuGet.org.

Pushes to `main` run the same verification and produce the same artifacts, but publish nothing. They
are versioned `0.0.0-edge.<run>.<attempt>` and exist only as workflow artifacts.

## Version rules

The version is the tag with the leading `v` removed. `v0.1.0` publishes `0.1.0`; `v0.1.0-rc.1`
publishes the prerelease `0.1.0-rc.1`. NuGet treats any version with a prerelease label as a
prerelease, so nothing else is needed to mark one.

A tag is refused unless the version:

- parses as a NuGet version;
- carries no build metadata (no `+`);
- is already in NuGet's normalized form.

The last rule is why `v1.2`, `v01.0.0`, and `v1.0.0.0` are rejected instead of being published as
`1.2.0` or `1.0.0`. A version NuGet rewrites no longer matches the tag consumers were told to pin.
Build metadata is rejected because NuGet drops it, so `v1.0.0+a` and `v1.0.0+b` would collide on one
package slot.

The rule lives in `eng/ServiceMantle.ReleaseTool` (`resolve-version`) and is unit tested there. The
workflow calls it rather than repeating it.

## What has to pass before anything is published

The publishing job cannot start until all of these have succeeded:

| Gate | What it proves |
| --- | --- |
| Tag ancestry check | The tag points at a commit contained in `main`. |
| `resolve-version` | The tag names a version NuGet will store unchanged. |
| `verify` (the full CI workflow) | The source builds and every registered test project passes. |
| `bootstrap-credential-existence` | The Windows-only Bootstrap existence evidence is in. |
| `publish` | Every package packed and passed `verify`: exact artifact set, IDs, versions, license, repository commit, dependency set, framework references, and matching internal versions. |

A failure or a cancellation in any of them leaves the publishing job unrun. The job has no
`always()` or `failure()` condition that could override that.

The packages pushed are the artifacts downloaded from the `publish` job, not a fresh build, so what
reaches NuGet.org is byte-for-byte what those gates inspected.

## Failures and reruns

A multi-package push is not a transaction. If a run is interrupted partway, NuGet.org holds some of
the set and not the rest. That is expected, and rerunning the same tag is the fix.

On a rerun, each package that is already on the feed at that version is inspected: the published
package's ID, version, and `repository/@commit` are compared against the release being published.

- **Same commit** - an earlier run of this same release pushed it. It is skipped as
  `already present`; its symbols are still attempted to repair a previous symbol upload failure.
- **Different commit, or no commit metadata** - that version belongs to something else. The package
  fails and nothing is overwritten.

A push conflict (HTTP 409) also requires that origin check. If the feed has not indexed the package
yet, the run fails explicitly; retry later after it becomes readable.

The run's closing summary lists every package under `published`, `already present`, or `failed`,
each with its ID and version, so a partial result is visible rather than reported as success. Any
failure - a rejected credential, an HTTP error, a missing artifact - exits non-zero.

Published versions are never deleted or replaced. If a released version is wrong, release a new
version.

To rehearse without pushing, run the command with `--dry-run`: it performs every check and every
feed comparison and pushes nothing.

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- publish \
  --version 0.1.0-rc.1 --commit "$(git rev-parse HEAD)" \
  --input artifacts/packages \
  --source https://api.nuget.org/v3/index.json \
  --api-key-environment SERVICEMANTLE_NUGET_API_KEY \
  --dry-run
```

## Post-publish verification

After a successful push the workflow restores the published version from NuGet.org into a NuGet
cache that has never held a ServiceMantle package, builds a minimal ASP.NET Core consumer against
it, and starts it. Nothing on the runner can make a broken or missing package look installable.

A push becomes restorable some time after the feed accepts it, so the first attempts are expected to
fail. The budget is finite - ten attempts, thirty seconds apart - and exhausting it fails the run
rather than waiting indefinitely.

## Credentials

Publishing uses NuGet.org Trusted Publishing. The publishing job exchanges its GitHub OIDC token for
a short-lived NuGet.org API key, so this repository stores no long-lived publishing secret.

`id-token: write` is granted to that one job and nowhere else. Every other job, and every job in the
pull-request CI workflow, runs with `contents: read`.

The short-lived key is passed to the release tool through an environment variable and travels to the
feed in an `X-NuGet-ApiKey` header, so it never appears in a process argument list. Everything the
publish command prints passes through a redactor keyed on that value.

## One-time NuGet.org configuration

These are account-side settings. They cannot be made from this repository, and publishing fails
until they exist:

1. A NuGet.org account that owns, or reserves, every package ID in `eng/packages.json`. As of this
   writing none of the `ServiceMantle*` IDs are registered on NuGet.org, so the ID prefix
   `ServiceMantle.*` should be reserved before the first release to keep it.
2. A Trusted Publishing policy on that account for each package, or for the reserved prefix, bound
   to:
   - repository owner `philfanzhou`
   - repository `ServiceMantle`
   - workflow file `release.yml`
   - environment `nuget.org`
3. A repository variable `NUGET_USER` holding that NuGet.org username. The login action passes it to
   the token exchange.
4. A GitHub environment named `nuget.org`. Add required reviewers to it if a release should need a
   human approval before it publishes.

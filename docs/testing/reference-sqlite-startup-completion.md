# Reference service SQLite startup completion

`ReferenceSqliteStartupCoordinator` runs the sample's opt-in SQLite startup deployment gate, and
`ReferenceSqliteStartupHostedService` is the startup step that calls it. This document describes one
property of that pair: when a finite startup result becomes visible, and what the gate promises -
and refuses to promise - about the caller's cancellation.

The gate is a consumer example. It is off unless it is switched on, it provides no cross-process
exclusion, and a `Ready` outcome is a schema gate, never `InstallationStatus.Completed`.

## The order a startup call follows

1. The deployment mode is authorized from declarations alone, the target is observed, and it is
   prepared only when that was explicitly permitted. None of that is changed here.
2. The migration runs inside one scope this call owns, holding the consumer's own context and
   executor.
3. **The scope is released.** The context and the executor in it are disposed before anything is
   published.
4. **Only then** is the caller's token read one last time.
5. Only after that checkpoint is a finite result published on the hosted service and written to the
   one log line this gate produces.

So a result the call had already computed is not published if the caller cancelled while the scope
was being released, and a release that fails leaves no `Ready` result and no success record behind.

## What a call reports

| Situation | Result |
| --- | --- |
| Work succeeds, release succeeds, caller has not cancelled | `Ready`, published and recorded once |
| Work succeeds, release succeeds, caller has cancelled by the checkpoint | `OperationCanceledException` with the caller's token; nothing published, nothing recorded |
| Work succeeds, release fails, caller has not cancelled | `MigrationFailed`, recorded once, carrying no release-exception text |
| Release throws an `OperationCanceledException` for some other token, caller has not cancelled | `MigrationFailed` |
| Release fails **and** the caller has cancelled | `OperationCanceledException` with the caller's token - cancellation wins |
| Migration did not succeed | `MigrationFailed`, after the scope was released |
| The work itself throws | the exception propagates, after this call's scope has been released; nothing is published or recorded |

The gate's one log line and its result carry the finite outcome only - never a path, a connection
string, an orchestrator message, or an exception's own text.

## What this does not guarantee

- **The window after the checkpoint.** A cancellation requested once the result is already on its
  way back to the caller is not caught.
- **Terminating a hung release.** A `DisposeAsync` that never returns is not forcibly ended. There is
  no overall startup timeout, no retry, and no resource-reclamation SLA.
- **Undoing anything.** A file that was published and a migration that was committed stay as they
  are; a cancelled startup is not a rolled-back one.
- **Diagnostics beyond this gate.** The claims above cover this coordinator's and hosted service's
  own result, log line, and exception. Logs a third party writes for itself are not covered.
- **A re-entrant startup protocol.** Each hosted startup instance is used the way a host uses it:
  one `StartingAsync` per instance.

The caller still owns the legality of the deployment, the target file, and every consumer
transaction.

## How it is covered

`ReferenceSqliteStartupCompletionTests` composes the gate through the real
`AddReferenceSqliteStartup` registration, over a real file, with the real SQLite preparation
provider, and calls the real `ReferenceSqliteStartupHostedService.StartingAsync`. Only the scoped
`IDatabaseMigrationExecutor` is replaced, by an adapter that reports a compatible schema - so no
migration runs - and whose release the test drives:

```csharp
services.AddScoped<IDatabaseMigrationExecutor>(_ => new ControlledScopedExecutor(async () =>
{
    entered.SetResult();
    await release.Task;      // the scope is still being released here
}));
```

That is enough to place a barrier inside the release and assert that neither the hosted service's
`Result` nor the gate's final log record exists yet, and then to have the release cancel the
caller's own source, fail, or do both. The adapter never runs a migration, so no case in that file
can change the file it was pointed at, and none of it depends on the separate cancellation boundary
of the sample's own migration executor.

`ReferenceSqliteStartupTests`, `ReferenceSqliteMigrationTests`, and
`ReferenceSqliteDeploymentEndToEndTests` remain the owners of the gate's other behaviour: it is off
by default, an unauthorized mode or unusable input is refused before any side effect, a missing
target is not created without permission, and the real subprocess deployment contract holds.

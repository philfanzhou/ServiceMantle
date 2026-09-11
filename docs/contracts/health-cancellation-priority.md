# Health readiness: caller cancellation priority

One readiness read has one output checkpoint per layer. This document states what happens at that
checkpoint when the caller has already cancelled, and what is deliberately left outside the promise.

It covers the two public exits of the same health pipeline:

- `ReadinessDecisionSource`, the default `IServiceReadinessDecisionSource`.
- The readiness endpoints `GET /health/ready` and `GET /health`, over **any** registered decision
  source.

`GET /health/live` is not part of this: it never resolves a readiness decision source.

## The rule

If, at the point where this layer would hand back its result, the caller's cancellation has been
requested, the read ends with an `OperationCanceledException` whose `CancellationToken` is the
caller's own token. That outranks every other outcome the read had already computed:

| The read's own outcome | Caller has cancelled | Result |
| --- | --- | --- |
| Snapshot source throws an ordinary exception | yes | `OperationCanceledException`, caller's token |
| Snapshot source throws `TimeoutException` | yes | `OperationCanceledException`, caller's token |
| Snapshot source throws somebody else's `OperationCanceledException` | yes | `OperationCanceledException`, caller's token |
| Snapshot source returns a snapshot, or `null` | yes | `OperationCanceledException`, caller's token |
| Decision source returns `Ready`, `NotReady`, `Unavailable`, or `null` | yes | request ends with `OperationCanceledException`, request's token - no response is written |
| Decision source throws anything | yes | request ends with `OperationCanceledException`, request's token |
| Any of the above | no | the existing finite classification, unchanged |

Without a caller cancellation nothing moves: an ordinary source failure is still
`health.probe_failed`, an internal budget timeout is still `health.probe_timeout`, a base snapshot
that is not ready is still projected with its own error code, and a ready snapshot still runs the
ordered contributors under their shared budget.

The default source keeps its existing cancellation exit: before it releases the linked token source
it owns, it cancels it, so a cooperative snapshot source is notified on the token it received. That
is true on the failure exits as well, not only on the completed-snapshot exit.

## What is not promised

- **The window after the checkpoint.** A cancellation requested between the checkpoint and the
  caller receiving the result is not caught. The checkpoint is a boundary, not a continuous guard.
- **Transport timing.** Nothing here bounds the delay between a client's socket FIN/RST and the
  server's request token being cancelled.
- **Third-party behaviour.** A source that ignores its token, blocks, or throws from a cancellation
  callback is not forcibly interrupted, and this layer does not wait for it to finish its own
  cleanup.
- **What a `TimeoutException` means.** A source that throws one is classified as a timeout. That is a
  classification, not proof that the configured budget was actually exhausted.
- **Diagnostics beyond this path.** The negative assertions cover the errors and responses this path
  produces, and what the tests capture from it. They say nothing about what a source logs for itself.

## How it is covered

`ServiceReadinessCancellationPriorityTests` drives both exits:

- The **source** exit is driven through the real default decision source, composed through public
  DI, with a snapshot source that cancels the caller's own token and then finishes in each of the
  ways above. Uncancelled control cases assert the finite classifications are unchanged, one
  controlled internal budget timeout asserts the timeout classification survives, and the fixture
  releases and awaits the probe it blocked.
- The **endpoint** exit is driven with a replaced decision source over both readiness routes,
  asserting on the exception the *handler* produced - observed by middleware around the pipeline -
  because an `HttpClient` cancelling itself would prove nothing about what the endpoint did.

An already-cancelled request resolves no decision source and invokes none; the live route resolves
none either.

# Consul registration lifecycle decision (#290)

Status: implemented by #49. This document remains the normative state, completion, and stop matrix;
the implementation lives in `ServiceMantle.Consul`.

## Decision

The Consul lifecycle consumes one provider-independent
`IServiceReadinessDecisionSource` from the core `ServiceMantle.Health` namespace. A decision contains
the exact immutable `ServiceHealthSnapshot` used by the evaluation, the final Ready value after the
base matrix and ordered readiness contributors, and only a bounded safe error code when not Ready.
Issue #321 owns that new public contract and changes the ASP.NET Core health endpoints to consume its
default adapter. Issue #49 is blocked by #321.

This is the only readiness input to the lifecycle. `ServiceMantle.Consul` does not reference
ASP.NET Core, issue an HTTP request to `/health/ready`, repeat the readiness algorithm, or accept an
independently supplied Boolean. A health endpoint request never starts or drives registration. The
consumer registers the same decision source for the HTTP health surface and the optional Consul
lifecycle; separate calls may sample different moments, but they use one source and one evaluation
contract.

```text
consumer-owned base health state + registered contributors
                         |
                         v
             IServiceReadinessDecisionSource       (core contract)
                         |
             +-----------+----------------+
             |                            |
             v                            v
   ASP.NET Core health endpoints    Consul lifecycle owner
                                          |
                                          v
                              snapshot-bound Consul session
```

The lifecycle is one hosted controller. Only its owner loop mutates state and starts Consul
operations. A single non-overlapping readiness sampler may publish decisions to that loop. The
sampler never performs Consul work, and all remote completions are returned to the owner loop. At
most one register or deregister operation is active for an instance.

## Startup and configuration ownership

`StartAsync` checks its caller token, calls `ConsulClientProvider.CreateClient()` exactly once, and
checks the caller token again before starting the controller:

| Provider outcome | Startup outcome | Owned resources |
| --- | --- | --- |
| `null` | Enter terminal `Disabled`; return successfully | No client, timer, sampler, or background loop |
| Session | Enter `NotReady` with remote presence `Absent`; start one sampler and owner loop | The lifecycle exclusively owns the session until stop |
| `SnapshotUnavailable` or `InvalidConfiguration` | Fail startup with the existing safe exception | No background work; dispose a session if one was produced |
| `ClientCreationFailed` | Fail startup with the existing safe exception; do not retry | No background work |
| Caller cancellation before or during startup | Propagate an `OperationCanceledException` carrying the original token | Dispose any session created before cancellation was observed |

The session captures one active setting snapshot and its version. All `consul.*` definitions are
restart-bound. Later active snapshot versions are not watched, rebound, or reconciled. A changed
endpoint, token, registration, disabled flag, or health URL takes effect only after a consumer-owned
process restart. The lifecycle does not call `CreateClient()` again.

The same session and registration ID are used for register retries, cleanup deregistration, and stop.
The lifecycle calls `Dispose()` once after the final remote operation settles or after the cooperative
shutdown budget expires. A disposal failure becomes a safe diagnostic and is not retried. Disposal
does not imply deregistration.

## State model

The finite control state is paired with a conservative remote-presence observation:

| State | Meaning | Allowed remote presence |
| --- | --- | --- |
| `Disabled` | The captured configuration is disabled; terminal until restart | `Absent` |
| `NotReady` | Enabled, latest decision is not Ready, and no operation is active | `Absent` or `Unknown` |
| `Registering` | One register operation is active | `Absent` or `Unknown` |
| `Registered` | A register operation returned `Success` while the latest desire is present | `Present` |
| `Deregistering` | One deregister operation is active | `Present` or `Unknown` |
| `Backoff` | No remote operation is active; an intent-specific retry delay is active | `Unknown`, or `Absent` for register retry |
| `Stopping` | Stop has priority; no new register may start | `Absent`, `Present`, or `Unknown` |

`Absent` means no successful registration has occurred since startup, or the latest completed
deregister returned `Success`. `Present` requires a completed register `Success` not followed by a
completed deregister `Success`. `Unknown` means a timeout, cancellation, `Rejected`, `Unavailable`,
undefined result, or incomplete operation may have had a remote side effect. In particular, register
timeout never means “not registered.”

The desired presence is derived only from the latest readiness event: Ready means `Present`; not
Ready, readiness failure, and stopping mean `Absent`. State, desired presence, remote presence,
attempt number, operation generation, and session ownership are changed only by the owner loop.

## Timing and retry policy

Issue #49 introduces one validated lifecycle options object with these exact defaults and inclusive
ranges:

| Option | Default | Minimum | Maximum | Purpose |
| --- | ---: | ---: | ---: | --- |
| Readiness poll interval | 1 s | 100 ms | 30 s | Delay between completed readiness samples |
| Readiness call budget | 10 s | 100 ms | 60 s | Outer budget for one decision-source call |
| Consul operation budget | 10 s | 100 ms | 30 s | Budget passed to and awaited for one register/deregister call |
| Initial retry delay | 250 ms | 50 ms | 5 s | First transport retry delay |
| Maximum retry delay | 5 s | Initial delay | 30 s | Exponential delay ceiling |
| Shutdown total budget | 15 s | 1 s | 60 s | Total cooperative cleanup time after stop begins |

Invalid, non-finite, conflicting, or out-of-range values fail host startup before a readiness sampler,
timer, or remote operation starts. Durations use `TimeProvider`; tests use fake time. Retry delay is
`min(maximum, initial * 2^failureCount)` with overflow-safe saturation. There is no jitter. A
successful operation or a change in desired presence resets the failure count.

Readiness failures are sampled again after the fixed poll interval and do not use transport backoff.
Register and deregister `Rejected`, `Unavailable`, undefined results, and internal timeouts use the
exponential transport backoff. Retries continue while the corresponding desire remains current, so
the number of attempts over an arbitrarily long process lifetime is deliberately not capped. What is
bounded is one operation, every delay, concurrency, and shutdown work. There is no guarantee of an
eventual successful registration or a maximum recovery time.

The existing default HTTP client retains its own 10-second timeout. The lifecycle operation budget
is an additional ownership bound and is also applied to replacement clients.

## Transition matrix

The tables below are normative. “Latest desire” includes any readiness event queued while an
operation was active.

### Readiness and steady states

| Current state | Event | Action and next state |
| --- | --- | --- |
| `Disabled` | Any readiness or stop event | Ignore readiness; stop is already complete; remain `Disabled` |
| `NotReady/Absent` | Not Ready, source exception, null/invalid decision, or internal readiness timeout | No remote call; remain `NotReady/Absent` with a safe readiness-unavailable diagnostic |
| `NotReady/Absent` | Ready | Start one register with a new generation; enter `Registering` |
| `NotReady/Unknown` | Not Ready | Start cleanup deregistration; enter `Deregistering` |
| `NotReady/Unknown` | Ready | Start idempotent register for the same registration ID; enter `Registering` |
| `Registered/Present` | Ready | No remote call; remain `Registered` |
| `Registered/Present` | Not Ready or readiness failure | Start deregistration immediately; enter `Deregistering` |
| Register `Backoff` | Ready | When the delay completes, start one register; enter `Registering` |
| Register `Backoff` | Not Ready | Cancel the delay; deregister if presence is `Unknown`, otherwise enter `NotReady/Absent` |
| Deregister `Backoff` | Not Ready | When the delay completes, start one deregister; enter `Deregistering` |
| Deregister `Backoff` | Ready | Cancel the delay; start register for the same ID; enter `Registering` |

The readiness sampler is fail-closed. A source exception, internally cancelled call, internal timeout,
null decision, or invalid/undefined decision is a not-Ready event. Caller cancellation of
`StartAsync` or `StopAsync` is not converted into this event; it retains its original token.

### Register completion

| Result | Latest desire | Presence and next state |
| --- | --- | --- |
| `Success` | Present | `Present`; enter `Registered`; reset backoff |
| `Success` | Absent | `Present`; immediately start deregister; never expose `Registered` as the settled state |
| `Rejected`, `Unavailable`, or undefined | Present | `Unknown`; enter register `Backoff` |
| `Rejected`, `Unavailable`, or undefined | Absent | `Unknown`; immediately start cleanup deregister |
| Internal operation timeout/cancellation | Present | Request cancellation, wait for cooperative settlement, then `Unknown` and register `Backoff` |
| Internal operation timeout/cancellation | Absent | Request cancellation, wait for cooperative settlement, then `Unknown` and cleanup deregister |
| Caller stop | Any | Apply the stopping rules below; never start another register |

### Deregister completion

| Result | Latest desire | Presence and next state |
| --- | --- | --- |
| `Success` | Absent | `Absent`; enter `NotReady`, or finish `Stopping` |
| `Success` | Present | `Absent`; immediately start register |
| `Rejected`, `Unavailable`, or undefined | Absent | `Unknown`; enter deregister `Backoff` |
| `Rejected`, `Unavailable`, or undefined | Present | `Unknown`; start register only after deregister has settled |
| Internal operation timeout/cancellation | Any | Request cancellation, wait for cooperative settlement, retain `Unknown`, then follow latest desire |
| Caller stop | Any | Continue cleanup under the remaining shutdown budget |

A Ready flip during deregistration never starts an overlapping register. The deregister must settle;
then registration re-establishes the desired record. A not-Ready flip during register requests
cancellation of that attempt, but the controller waits for it to settle before deregistering. This
ordering prevents a late deregister from deleting a newer registration and prevents a late register
from escaping cleanup.

Each operation and readiness sample has a monotonically increasing generation. A completion whose
generation is no longer the active generation cannot directly select `Registered` or `NotReady`.
The owner records only its conservative presence implication and applies the latest desired state.
Events delivered after stopping has completed are ignored.

## Stop matrix

Stop cancels the sampler and every backoff delay first. It establishes desired presence `Absent` and
starts the shutdown total budget. No new register operation may start after this point.

| State when stop begins | Shutdown action |
| --- | --- |
| `NotReady/Absent` | Dispose the session; complete stop |
| `Registered/Present` | Start deregister and retry within the remaining shutdown budget |
| `Backoff` with `Present` or `Unknown` | Cancel delay; start deregister within the remaining budget |
| `Registering` | Cancel register, await settlement, then deregister because presence may be `Present` or `Unknown` |
| `Deregistering` | Await the current attempt; retry only while budget remains |
| `NotReady/Unknown` | Attempt deregistration within the remaining budget |

If deregistration succeeds, the lifecycle disposes the session and completes. If the internal
shutdown budget expires, it stops creating operations, emits a safe `shutdown_timeout`
classification, disposes only after any cooperative in-flight operation has settled, and completes
without claiming remote absence. If the `StopAsync` caller token cancels first, the original token is
propagated and no new work starts; best-effort ownership cleanup follows the same non-overlap rule.

## Failure and diagnostic classification

Diagnostics are finite classifications and metadata only: control state, attempt count, captured
snapshot version, operation kind, and safe result category. They never contain the ACL token,
registration body, endpoint, health URL, address, service name, registration ID, raw exception, or
response body.

| Input | Classification and behavior |
| --- | --- |
| Readiness source failure, null/invalid result, or internal cancellation | `readiness_unavailable`; desire becomes absent |
| Readiness call budget expires | `readiness_timeout`; desire becomes absent |
| Register `Rejected` | `register_rejected`; presence `Unknown`; retry if still Ready |
| Register `Unavailable`, undefined result, or internal cancellation | `register_unavailable`; presence `Unknown`; retry if still Ready |
| Register operation budget expires | `register_timeout`; presence `Unknown`; preserve non-overlap |
| Deregister `Rejected` | `deregister_rejected`; presence `Unknown`; retry according to latest desire |
| Deregister `Unavailable`, undefined result, or internal cancellation | `deregister_unavailable`; presence `Unknown`; retry according to latest desire |
| Deregister operation budget expires | `deregister_timeout`; presence `Unknown`; preserve non-overlap |
| Shutdown total budget expires | `shutdown_timeout`; do not claim absence |
| Session disposal throws | `session_disposal_failed`; do not retry or claim that disposal deregistered |

The existing `ConsulClientSession` already converts replacement-client exceptions, internal
cancellation, and undefined enums to `Unavailable`, while propagating cancellation of the token it
was given. The lifecycle distinguishes its own timeout token from `StartAsync`/`StopAsync` caller
tokens before selecting a classification.

Synchronous `IConsulClientFactory.Create()` and `IDisposable.Dispose()` cannot be forcibly cancelled.
A replacement `IConsulClient` may also ignore cancellation. ServiceMantle does not promise a hard
wall-clock bound for such non-cooperative code. It never starts an overlapping remote operation or
disposes a client concurrently with its unfinished operation merely to manufacture a timeout.

## Required verification for #49

All lifecycle tests use a fake `TimeProvider`, scripted readiness decisions, a scripted session, and
operation barriers. The minimum matrix is:

- disabled startup proves zero client resolution, sampler, timer, loop, and remote calls;
- every base non-Ready matrix value and every contributor rejection/failure proves zero register;
- Ready registers once, repeated Ready does not duplicate it, and a later not-Ready decision
  deregisters the same ID;
- barriers flip readiness during register and deregister and prove no overlapping remote calls,
  latest-desire handling, and stale-generation handling;
- each finite transport result, exception, internal cancellation, undefined enum, and timeout proves
  the presence classification, exact retry delay, saturation at the maximum, and safe diagnostics;
- stop is exercised from every state, including backoff and both in-flight operations; it proves the
  shutdown total budget, caller-token precedence, successful cleanup, and unknown-presence outcome;
- session creation, startup caller cancellation, disposal failure, and a non-cooperative replacement
  client prove the documented ownership boundary;
- concurrent lifecycle notifications remain serialized, with a recorded maximum of one active
  Consul operation;
- captured snapshot version and session remain fixed after a newer active setting snapshot appears;
  no hot-reload behavior is asserted;
- all exception, result, diagnostic, serialization, and `ToString()` projections are checked against
  a sentinel token.

Existing single-call transport tests remain the evidence for HTTP method, path, body, token header,
redirect behavior, and the default 10-second client timeout. Lifecycle tests do not claim validation
against a real Consul cluster.

## Explicit non-guarantees

- A successful agent response does not guarantee propagation to every catalog or DNS reader.
- A successful deregistration does not prove that all business traffic has drained.
- Process termination, machine failure, network partition, an unknown remote outcome, or a
  non-cooperative replacement client cannot be rolled back or forcibly completed.
- Total retry attempts over the process lifetime and recovery time are not bounded. Per-call,
  per-delay, concurrency, and cooperative shutdown budgets are bounded as specified above.
- There is no configuration hot reload, cross-instance coordination, idempotency key beyond the
  stable Consul registration ID, or compensation for an unknown result.
- The lifecycle does not turn readiness contributors into schedulers and does not trigger setup,
  migration, database creation, configuration refresh, or business writes.
- Token secrecy covers ServiceMantle-owned diagnostics and projections. It does not cover deliberate
  access by a custom factory, external HTTP instrumentation, a debugger, process memory, or Consul
  agent logs.

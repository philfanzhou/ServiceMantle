# Reference service base telemetry acceptance

`ReferenceTelemetryTests` accepts the reference sample's opt-in base instrumentation through the
sample's own `ReferenceApplication.CreateBuilder` / `Build` seam, on a real loopback Kestrel host.
There is no test double for the composition: the switch, the providers, the resource, and the
lifetime all come from the code the sample actually runs.

## What is wired

`ReferenceService:Telemetry:Enabled` defaults to `false`. Only a value that parses to `true`
registers anything. When it does, the sample calls `AddOpenTelemetryInstrumentation` on the existing
`ServiceMantle.OpenTelemetry` package with its defaults - ASP.NET Core tracing, `HttpClient` tracing,
and .NET runtime metrics - and adds no options system of its own.

The OpenTelemetry resource is reused, not built: it is exactly the `service.name`, `service.version`,
and `service.instance.id` that `AddServiceMantle` and `ServiceLogContext` already established.

## What is not wired, and is not implied

No OTLP exporter, no Prometheus endpoint or authorization, no `ServiceMantleMetrics`, no health
endpoint, no health source, no Consul, and no fixed service or installation phase metric. The switch
fabricates no phase, and it does not turn the sample into a service that reports readiness.
`/metrics`, `/health`, and `/management` return 404 whether the switch is on or off. Those
capabilities remain with [#158](https://github.com/philfanzhou/ServiceMantle/issues/158),
[#156](https://github.com/philfanzhou/ServiceMantle/issues/156), and
[#109](https://github.com/philfanzhou/ServiceMantle/issues/109); nothing here is evidence that any of
them is done.

The existing logging switch is untouched. With logging on, correlation and Problem Details behave
exactly as before, and telemetry introduces no second authentication policy and no fake `Ready`.

## The matrix

| Case | Explicit inputs | Evidence |
| --- | --- | --- |
| Unauthorized switch | absent, `false`, empty, `yes`, `1`, `not-a-boolean` | no `TracerProvider`, no `MeterProvider`, no sample registration marker; `Microsoft.AspNetCore` and `System.Net.Http` have no listener; a `System.Runtime` instrument is not enabled; `GET /` still 200; `/metrics`, `/health`, `/management`, `/setup` still 404; the directory stays empty, so no database file is created |
| Enabled, with test collectors | `true` | exactly one `TracerProvider` and one `MeterProvider`; a real loopback request produces one server span and one client span; a reader the test owns collects a `System.Runtime` metric signal |
| Enabled, no test collectors | `true` | both providers exist, the test installs no reader, processor, or exporter, the request is served, and no span or export is observed - the metric evidence above genuinely comes from the test's own reader |
| Resource identity | `true` | both providers carry exactly `service.name`, `service.version`, `service.instance.id` and nothing else; an unrelated configuration secret is in neither |
| Switch composition | logging × telemetry, all four rows | each starts, serves `GET /`, and stops; the correlation Header appears with logging on and not otherwise; providers appear with telemetry on and not otherwise |
| Pre-cancelled start | `true`, cancelled token | `StartAsync` throws a cancellation; `ApplicationStarted` is never signalled; no span, no export |
| Cancelled request | `true` | the caller's cancellation stays a cancellation rather than becoming a transport error, and the host serves the next request |
| Controlled disposal failure | `true`, an instrumentation that throws once on dispose | the failure reaches the caller instead of being swallowed; the fixture then releases the handles it owns, and the process-wide listeners are detached |
| Equivalent repeated registration | `true`, registered twice | still one `TracerProvider`, one `MeterProvider`, one instrumentation instance, and one server span per request |
| Conflicting extra registration | `true` plus a registration that disables runtime metrics | the public package's own start-up validation refuses it; `ApplicationStarted` is never signalled and no listener is attached. The sample does not bypass or weaken that validation |
| Restored graph | - | the sample's `project.assets.json` contains the base instrumentation packages, and also the OTLP and Prometheus exporter drivers that `ServiceMantle.OpenTelemetry` now ships in one package |
| Enabled composition | `true` | the sample's container holds no service owned by `ServiceMantle.OpenTelemetry.Otlp`, `ServiceMantle.OpenTelemetry.Prometheus`, or `OpenTelemetry.Exporter`, so a driver in the graph is still not an activated exporter |

## Isolation

`ActivitySource` and `Meter` listener state is process-wide, not scoped to one host, so these tests
run in their own non-parallel xUnit collection. The collection is declared on the test class rather
than on the assembly, so the rest of `tests/ServiceMantle.ReferenceService.Tests` keeps the
parallelism it already had.

Each case owns its own temporary directory, its own `HttpClient`, its own `ActivitySource` and
`Meter` handles, and removes them on dispose - including after a disposal that failed on purpose.

## What is not guaranteed

- **"Off" means the ServiceMantle-owned providers, listeners, and controlled export registrations are
  not activated.** It does not mean the static dependency disappears from the published output, and
  it is not a claim that the whole .NET process runs no thread, timer, or socket.
- The resource allow-list covers the three existing non-secret identity fields. It is **not** a
  redaction or low-cardinality guarantee for every span and metric attribute. The sample collects,
  transforms, and adds no Header, body, query, or connection field, but instrumentation attributes
  produced by the libraries themselves are theirs.
- The secret negative assertion covers a synthetic configuration value unrelated to the call path
  staying out of this wiring's resource, its own diagnostics, and the test's captured output. It does
  not promise that a secret embedded in an arbitrary URL or a third-party attribute is scrubbed.
- Nothing here promises a sampling rate, a throughput figure, a particular runtime counter value,
  a successful export, or a forced release on abrupt termination. The disposal checks cover the
  resources this wiring actually owns, on a normal stop and dispose.
- Caller cancellation is kept distinct from internal failure. Forcing a non-cooperative third-party
  instrumentation to stop is not promised.
- Base instrumentation creates no remote export target.

## Running the tests

```bash
dotnet build ServiceMantle.slnx -c Release
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release --no-build --no-restore
```

No container, no environment variable, and no network beyond loopback is needed, and none of these
tests is ever skipped.

To run this acceptance alone:

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release \
  --filter-class "ServiceMantle.ReferenceService.Tests.ReferenceTelemetryTests"
```

## Related

- [`samples/ServiceMantle.ReferenceService/README.md`](../../samples/ServiceMantle.ReferenceService/README.md) -
  the sample's own description of the switch.
- [`reference-sqlite-deployment.md`](reference-sqlite-deployment.md) - the sample's other explicit
  startup switch.

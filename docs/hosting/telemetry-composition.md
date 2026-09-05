# OpenTelemetry instrumentation on the HTTP pipeline

> Implementation status: the HTTP composition matrix is blocked by
> [issue #300](https://github.com/philfanzhou/ServiceMantle/issues/300). On the current baseline,
> a registration that enables runtime metrics followed by conflicting options can create metrics
> instrumentation before startup reports the conflict. The regression tests intentionally retain
> the failing zero-factory assertions. Do not treat this draft as evidence that the entire matrix
> is satisfied.

Use the existing `ServiceMantleBuilder.AddOpenTelemetryInstrumentation` extension before `Build`,
then compose the required HTTP middleware with `UseServiceMantlePipeline`. No additional Host
builder, automatic package registration, exporter, authentication or logging Host is required.

## Selection matrix

A is `EnableAspNetCoreTracing`, H is `EnableHttpClientTracing`, and R is `EnableRuntimeMetrics`.
The three selectors and `Enabled` default to `true` when the extension is called; omitting the call
registers no instrumentation. Disabled registrations normalize all selectors to false.

| Registration | A | H | R | TracerProvider | MeterProvider | Result |
| --- | --- | --- | --- | --- | --- | --- |
| Absent | — | — | — | None | None | HTTP 200, Stop, Dispose |
| Enabled=false | 0 | 0 | 0 | None | None | HTTP 200, Stop, Dispose |
| Enabled=false | 1 | 1 | 1 | None | None | HTTP 200, Stop, Dispose |
| Enabled=true | 1 | 0 | 0 | One | None | Incoming tracing |
| Enabled=true | 0 | 1 | 0 | One | None | Outgoing HttpClient tracing |
| Enabled=true | 0 | 0 | 1 | None | One | Runtime metrics |
| Enabled=true | 1 | 1 | 0 | One | None | Both tracing sources |
| Enabled=true | 1 | 0 | 1 | One | One | Incoming tracing and runtime metrics |
| Enabled=true | 0 | 1 | 1 | One | One | Outgoing tracing and runtime metrics |
| Enabled=true | 1 | 1 | 1 | One | One | All three signals |
| Enabled=true | 0 | 0 | 0 | None | None | Start fails |
| Equivalent repeat | Same effective choices | | | At most one | At most one | No duplicate request spans or instrumentation ownership |
| Conflicting repeat | Different effective choices | | | Must not activate | Must not activate | Start fails; metrics activation ordering currently blocked by #300 |

Disabled-to-enabled and enabled-to-disabled repeats are conflicts. Two disabled registrations with
different selectors are equivalent. For successful rows, provider creation is selected by signal:
A or H requires tracing; R requires metrics. Instrumentation creates no export destination. Runtime
collection/export configuration is separate; the test uses a manual in-memory reader only for R rows.

## Executable local wiring

Reference `ServiceMantle.OpenTelemetry` (which brings the AspNetCore integration) in an ASP.NET Core
application. This local example uses an explicitly fixed Ready snapshot to isolate composition.
A real service must provide its own cancellation-aware `IServiceHealthSnapshotSource` reflecting
its authoritative installation, migration and database state; registering instrumentation does not
supply or persist that state.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5081");
var mantle = builder.Services.AddServiceMantle(
    ServiceId.Parse("telemetry-composition"), InstanceId.Parse("telemetry-01"), serviceVersion: "2.3.4")
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddServiceMantlePhaseGate();
builder.Services.AddSingleton<IServiceHealthSnapshotSource, DemoSnapshotSource>();

// Omit this call entirely for the absent row. Set Enabled=false for disabled rows.
mantle.AddOpenTelemetryInstrumentation(options =>
{
    options.Enabled = true;
    options.EnableAspNetCoreTracing = true;
    options.EnableHttpClientTracing = true;
    options.EnableRuntimeMetrics = true;
});

await using var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapGet("/ok", () => Results.Ok()).RequireServiceMantleSecurityResponseHeaders();
await app.StartAsync();
try
{
    using var client = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5081/ok");
    request.Headers.Add("x-correlation-id", "telemetry-request-correlation");
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    // A long-running service can await app.WaitForShutdownAsync() here.
}
finally
{
    await app.StopAsync();
}
// await using disposes the Host-owned providers and their instrumentation.

sealed class DemoSnapshotSource : IServiceHealthSnapshotSource
{
    public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable));
    }
}
```

Configure all options before Build. Call the pipeline once; do not insert its individual middleware
again. Optional forwarded-header trust, endpoint rate-limit policies and security/authorization
metadata remain explicit. No health endpoint, Cookie scheme, Serilog Host or exporter is installed
by this example. Its only HTTP connection is the local test request.

## HTTP behavior and identity

The pipeline order is unchanged: configured forwarding, correlation, Problem Details, routing,
security headers, phase gate, optional authentication, rate limiting, optional authorization, then
consumer handlers. Telemetry does not bypass Gate: an unready snapshot still returns the existing
503 JSON, while an unhandled exception before response start returns 500 Problem Details. Marked
endpoints retain the security Header baseline, and `x-correlation-id` is preserved on successful,
exception and Gate responses. A caller-aborted request remains cancellation, not a telemetry
configuration error. The test's source-entry/handler-entry barriers explicitly bind caller
cancellation to the server request token; they do not promise a TCP disconnect notification latency.

Both providers carry exactly the ServiceMantle-owned Resource fields `service.name`, `service.version`
and `service.instance.id`, matching `ServiceLogContext`. Use non-secret Host identity metadata.
The Correlation ID remains separate from W3C trace identity; instrumentation does not rewrite it to
a Trace ID. Upstream HTTP/runtime instrumentation owns its span and metric attributes: the Resource
whitelist is not an arbitrary attribute sanitization or cardinality guarantee.

## Ownership and evidence

`ServiceMantleTelemetryPipelineTests` exercises Build, pipeline composition, mapping, Start, actual
loopback HTTP, Stop and Dispose. For each selected tracing signal it observes ended request spans
in memory; for R it attaches a manual reader and records a controlled `System.Runtime` counter.
Collectors are configured only where the ServiceMantle registration already declares a provider;
absent and disabled rows do not gain a provider through test setup. A separate full-instrumentation
row runs with no test reader, processor or exporter.

Normal Stop plus Dispose removes the observed ActivitySource and Meter listeners, and repeated
Dispose does not repeat successful instrumentation disposal. A pre-cancelled Start does not report
`ApplicationStarted`. An instrumentation Dispose exception remains visible. After such an exception,
the fixture explicitly cleans up the handles it still owns; arbitrary failures need not release all
remaining SDK resources, and retrying failed disposal need not call instrumentation only once.
Tests use assembly-wide serialization for global listeners and manual metric collection rather than
sleep-based periodic export checks.

The new tests also inspect Core/AspNetCore restored dependency graphs for transitive telemetry
references. Existing OpenTelemetry package tests check that the base package and its restored graph
contain no Exporter or Prometheus driver. These packages retain their current dependency boundaries;
this composition does not add packages, framework references or `eng/packages.json` entries.

## Limits

- Absence claims cover ServiceMantle provider/listener creation and controlled export-work counters,
  not every .NET process thread, timer or network connection, nor consumer-owned providers.
- No OTLP, Prometheus, fixed ServiceMantle metrics, Consul, remote backend, database, authentication or
  logging Host is added. Their independent tasks and packages are outside this matrix.
- No guarantee is made about export success, sampling, throughput, cross-process propagation, packet
  loss, forced-termination cleanup, or interruption of third-party code that ignores cancellation.
- Unrelated configuration secrets are asserted absent from captured test logs and safe diagnostics.
  This does not guarantee universal URL/Header/SQL/endpoint or third-party attribute sanitization.
- No product identity, transaction, migration, persistence or cross-request state ownership is
  transferred to the library, and no concurrent DI/options/endpoint mutation is supported here.

The startup activation requirement remains incomplete until #300 is resolved. Keep that failure
visible; bypassing framework metrics or skipping conflict assertions would not validate this
composition. This work is otherwise independent of the core optional composition in #117 and any
reference service or exporter task.

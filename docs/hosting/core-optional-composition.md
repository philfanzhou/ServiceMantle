# Core optional capabilities on the HTTP pipeline

Configure management Cookie authentication (A), health endpoints (H), and Serilog Console (L)
explicitly before `Build`. The existing `ServiceMantleBuilder` is the registration entry point;
there is no additional composition facade or package scanning. The required HTTP pipeline remains
active in every combination.

| A | H | L | Optional behavior |
| --- | --- | --- | --- |
| 0 | 0 | 0 | No management Cookie scheme, health routes, or ServiceMantle Serilog lifecycle |
| 0 | 0 | 1 | Serilog Console only |
| 0 | 1 | 0 | Health routes only |
| 0 | 1 | 1 | Health routes and Serilog Console |
| 1 | 0 | 0 | Management Cookie authentication only |
| 1 | 0 | 1 | Management Cookie authentication and Serilog Console |
| 1 | 1 | 0 | Management Cookie authentication and health routes |
| 1 | 1 | 1 | All three capabilities |

## Executable local wiring

This example uses `ServiceMantle.AspNetCore` and `ServiceMantle.Serilog`. Change the three booleans
to run each row. The fixed Ready source and ephemeral Data Protection provider are local demo/test
choices: replace them with consumer-owned state observation and an appropriate key-storage policy
for a deployed service. The example does not implement sign-in or an identity provider.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

var authentication = true;
var health = true;
var logging = true;
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
var mantle = builder.Services.AddServiceMantle(
    ServiceId.Parse("composition"), InstanceId.Parse("composition-01"), serviceVersion: "1.2.3")
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Composition-Secret"])
    .AddRateLimiting()
    .AddServiceMantlePhaseGate();

// The required Gate needs state even when optional health endpoints are absent.
builder.Services.AddSingleton<IServiceHealthSnapshotSource, DemoSnapshotSource>();
if (authentication)
{
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    mantle.AddManagementCookieAuthentication();
}
if (health) mantle.AddServiceMantleHealthEndpoints();
if (logging) builder.AddServiceMantleSerilog();

await using var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapGet("/ok", (HttpContext context) =>
{
    var safeHeaders = context.RequestServices
        .GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>()
        .Project(context.Request.Headers);
    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Composition")
        .LogInformation("Composition handled {@Headers}", safeHeaders);
    return Results.Ok();
}).RequireServiceMantleSecurityResponseHeaders();
if (authentication)
{
    app.MapServiceMantleManagementGroup().MapGet("/protected", () => Results.Ok())
        .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management)
        .RequireServiceMantleManagementAdmin()
        .RequireServiceMantleSecurityResponseHeaders();
}
if (health) app.MapServiceMantleHealthEndpoints();

await app.StartAsync();
try
{
    using var client = new HttpClient();
    using var response = await client.GetAsync("http://127.0.0.1:5080/ok");
    response.EnsureSuccessStatusCode();
    // A long-running service can await app.WaitForShutdownAsync() here.
}
finally
{
    await app.StopAsync();
}
// await using disposes the Host and its owned services after Stop.

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

The production management Cookie requires HTTPS (`SecurePolicy.Always`). Tests create protected
tickets directly and attach them manually to loopback requests; this is not a browser login flow
and does not weaken the Cookie transport policy. Ephemeral keys disappear when the process exits.
Shared keys, state persistence, identity verification, login/logout, migrations, transactions, and
`DbContext` saving remain consumer responsibilities.

## Request and lifecycle behavior

`UseServiceMantlePipeline` requires the five registrations shown above and runs configured forwarding,
correlation, Problem Details, routing, security headers, the phase gate, optional authentication,
rate limiting, and optional authorization in the existing relative order. Call it once before consumer
handlers and map health once when enabled. Do not insert the individual middleware again. Forwarding
requires its own explicit trust configuration; endpoint authorization, security-header and named
rate-limit metadata remain explicit.

With A enabled and `Completed + Succeeded + Reachable`, missing Cookie, a valid non-Admin identity,
and a valid Admin identity receive 401, 403, and 200 respectively at the protected handler. The
phase gate runs first: a valid Admin Cookie cannot bypass an unready phase's 503. Without A, this
example neither registers the Cookie scheme nor maps the protected handler; the required pipeline
still handles `/ok`.

With H enabled, `/health/live` returns 200 without resolving the snapshot source. `/health/ready`
and `/health` read one snapshot per request and return 200 only for `Completed + Succeeded + Reachable`;
all other defined state combinations return 503. These library health endpoints bypass Gate sampling.
Missing or throwing sources produce `health.probe_failed`; an internal probe timeout produces
`health.probe_timeout`. A caller-aborted request remains cancellation. Without H, none of these three
routes is mapped and no health polling is added. The Gate still reads state for ordinary endpoints.
There is no shared Gate/health cache or cross-request/cross-instance consistency guarantee.

With L enabled, the correlation middleware's request scope reaches the Serilog logger with
`ServiceName`, `ServiceVersion`, `InstanceId`, and `CorrelationId`. Structured `Password` values and
denied `X-Composition-Secret` values passed through the request projector become `[REDACTED]`.
`AddSensitiveHeaders` and `AddServiceMantleSerilog` can appear in either order. No Header is
implicitly logged. The Serilog entry point replaces preexisting Microsoft logging providers; any
intentional additional providers need explicit registration afterward.

Equivalent repeated A/H/L registrations produce one effective Cookie scheme and Serilog lifecycle;
health still needs exactly one mapping call. Unsafe Cookie settings, out-of-range health timeouts,
invalid Serilog levels, or conflicting repeated settings fail before successful Host startup.
A pre-cancelled startup does not signal `ApplicationStarted`.

Stop and Dispose initiate one Serilog flush. Sink disposal exceptions are best effort and do not
replace shutdown; a blocked sink cannot hold shutdown beyond the configured flush wait (default two
seconds). Timeout does not terminate the sink: it may finish later, and a test-owned blocked sink
must be released and awaited by the fixture. Forced termination cannot guarantee flushing.

## Evidence and limits

`ServiceMantleCoreOptionalCompositionTests` runs all eight rows through Build, pipeline composition,
mapping, Start, real loopback HTTP, Stop and Dispose. It additionally covers all defined health
snapshots, safe failure responses, barrier-triggered cancellation, Console output in both registration
orders, duplicate/conflicting registration, pre-cancelled startup, and controlled sink disposal.
The cancellation fixture explicitly binds caller cancellation to the server request token after the
source-entry barrier; TCP half-close notification timing is not part of the assertion. Existing
AspNetCore and Serilog dependency tests verify the registered package boundaries: Core and
AspNetCore do not acquire Serilog, EF, database-driver, telemetry, or remote-sink dependencies.

Absence assertions concern the selected capability's DI objects, scheme, endpoints, snapshot reads,
and controlled lifecycle, not zero process threads/timers or absence of every optional assembly.
Loopback HTTP is fixture traffic. Other consumer-registered logging or background services are outside
these observations. No database, remote sink, exporter, Consul, or product management API is involved.
Configuration is fixed before Build; concurrent DI/options/mapping mutation is unsupported here.

Secret assertions cover the named structured fields, projector output, controlled sanitized logs,
and existing safe error responses. They do not cover arbitrary framework events, caller-interpolated
message templates, free text, or paths bypassing the sanitizer. See
[the structured logging security contract](../../LOGGING_SECURITY.md). This composition does not
change Cookie JSON, health responses, Gate paths, mandatory middleware order, or security guarantees
for unmarked endpoints and already-started responses. It does not promise cancellation of synchronous
third-party callbacks, remote delivery, or complete resource cleanup after arbitrary Dispose failures.

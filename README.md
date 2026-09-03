# ServiceMantle

ServiceMantle is a shared .NET 10 library for reusable service-management foundations used by ASP.NET Core services.

Current status: **early development**. Service identity, installation phase primitives, the one-time installation Setup Code lifecycle, the instance-local Bootstrap file model, database migration orchestration, the PostgreSQL advisory lock provider, structured logging identity context, the immutable sensitive request Header registry and diagnostic projection, the mandatory-sanitizing Serilog Host and Console defaults, the optional bounded Grafana Loki sink, core OpenTelemetry instrumentation, the optional OTLP trace and metric exporter, the isolated authorized Prometheus endpoint, the explicit forwarded-header trust boundary, the isolated setup and management rate-limit policies, the mandatory security response-header baseline, the request Correlation ID middleware, safe Problem Details exception mapping, the optional database target preparation capability (PostgreSQL server-database preparation), product-agnostic management audit persistence, and the management identity and authorization contract are implementation-complete, pending CI container verification (real PostgreSQL Testcontainers run in GitHub Actions, not locally). Management login and session flows, additional telemetry exporters, service discovery, and broader observability capabilities are not yet implemented.

`ServiceId` is a stable deployment-level identifier shared by all instances of one service. `InstanceId` identifies one running instance for runtime diagnostics and must not be used as a substitute for `ServiceId`.

The audited SignaCore migration/deletion gate is documented in [docs/signacore-legacy-migration/README.md](docs/signacore-legacy-migration/README.md); its machine-readable inventory pins the reviewed SignaCore commit, behavior evidence, replacement mapping, retained product boundaries, blockers, and staged #106 deletion tasks.

## Phase 1 scope

- bootstrap
- installation
- configuration
- administration abstraction
- auditing
- observability
- discovery

## Structured logging identity context

`ServiceMantle.AspNetCore` registers a singleton `ServiceLogContext` with the host identity. Its standard `ILogger` scope always emits `ServiceName`, `ServiceVersion`, and `InstanceId` as structured fields. `ServiceName` is the normalized `ServiceId`; `ServiceVersion` can be supplied explicitly and otherwise falls back to the entry assembly informational version, assembly version, then `unknown`.

```csharp
builder.AddServiceMantle(
    ServiceId.Parse("catalog"),
    InstanceId.Parse("catalog-01"),
    serviceVersion: "1.2.3");

var context = app.Services.GetRequiredService<ServiceLogContext>();
using (context.BeginScope(logger, new Dictionary<string, object?>
{
    ["Operation"] = "bootstrap",
}))
{
    logger.LogInformation("Starting operation");
}
```

Extension fields are limited to 32, require non-null values and identifier-style names, and cannot duplicate or override the four protected identity fields `ServiceName`, `ServiceVersion`, `InstanceId`, and `CorrelationId` (matching is case-insensitive). Disposing the returned handle ends the scope; standard `ILogger` async scope semantics keep concurrent execution contexts isolated. This context does not sanitize extension values or configure a logging sink, so callers remain responsible for passing only values safe for their providers.

## Consumer reference skeleton

[`samples/ServiceMantle.ReferenceService`](samples/ServiceMantle.ReferenceService/README.md) is a
minimal consumer-owned host with its own DbContext, migration, setting definitions and contributor
implementations. It exposes only a skeleton root response and does not initialize a database,
create administrators, or activate downstream capabilities at startup. Its external management
identity provider remains explicitly unconfigured. See the sample README for ownership boundaries,
smoke tests and the separate integration tasks; this is not a production template.

## Core OpenTelemetry instrumentation

Install the optional `ServiceMantle.OpenTelemetry` package and opt in after the host identity is
registered:

```csharp
builder.Services
    .AddServiceMantle(
        ServiceId.Parse("catalog"),
        InstanceId.Parse("catalog-01"),
        serviceVersion: "1.2.3")
    .AddOpenTelemetryInstrumentation();
```

The registration creates one tracing provider for ASP.NET Core and `HttpClient` instrumentation and
one metrics provider for .NET runtime instrumentation. It adds no exporter, network destination,
background export worker, sampling policy, or batching policy. Omitting the call leaves both
providers unregistered. A deployment can keep an otherwise shared composition path disabled, or
select individual instrumentations explicitly:

```csharp
serviceMantle.AddOpenTelemetryInstrumentation(options =>
{
    options.Enabled = false;
    options.EnableAspNetCoreTracing = true;
    options.EnableHttpClientTracing = true;
    options.EnableRuntimeMetrics = true;
});
```

ServiceMantle replaces the OpenTelemetry resource with exactly `service.name`, `service.version`, and
`service.instance.id`, using the same values as `ServiceLogContext.ServiceName`, `ServiceVersion`, and
`InstanceId`. These options cannot add resource attributes. Equivalent repeated registrations are
idempotent; an enabled registration that selects no instrumentation or repeated registrations with
different effective settings fail when the host starts. Providers and their instrumentation are
disposed with the host, and disposal failures remain observable to the caller.

Instrumentation-generated span and metric attributes are controlled by the upstream OpenTelemetry
packages, not by the ServiceMantle resource whitelist. This package does not guarantee their
sensitivity or cardinality, export success, sampling behavior, or any mapping between W3C trace
identity and the ServiceMantle Correlation ID.

## Fixed service and installation metrics

Opt in with `serviceMantle.AddServiceMantleMetrics()`. This creates a metrics provider and one
host-owned `ServiceMantleMetrics` publisher; it is independent of
`AddOpenTelemetryInstrumentation()` and adds no exporter, database polling or background work.
Repeating the call is idempotent. Omitting it leaves these instruments unregistered.

The meter is `ServiceMantle`, contract version `1.0.0`. Both observable gauges use unit `1`:

| Instrument | Point tags | Values |
| --- | --- | --- |
| `servicemantle.service.info` | None | Always 1 |
| `servicemantle.installation.phase` | `phase` only | Four 0/1 points: `unknown`, `bootstrap_configuration`, `pending_setup`, `completed` |

Each phase collection is one-hot, initially `unknown`. Identity is carried only by the existing
immutable resource fields `service.name`, `service.version`, and `service.instance.id`. The consumer
publishes a phase after observing its authoritative state, and clears it when that state is unknown:

```csharp
var metrics = serviceProvider.GetRequiredService<ServiceMantleMetrics>();
metrics.SetPhase(ServiceStartupPhase.PendingSetup);
// After the consumer confirms committed setup completion:
metrics.SetPhase(ServiceStartupPhase.Completed);
// If the consumer can no longer determine the current phase:
metrics.SetUnknown();
```

There is no request/user/credential/custom-tag or arbitrary phase-string API. Invalid enum values
throw a safe exception without replacing the previous observation. Concurrent updates and collection
preserve a complete one-hot phase set. The SDK view explicitly allows only the declared point tags
and limits information/phase cardinality to 1/4 respectively; the publisher emits exactly five
series per host resource. Different hosts in one process export only their own Meter objects;
external same-name meters are dropped. Host disposal closes only that host's publisher.

Identity fields must be non-secret deployment metadata. These bounds do not cover fleet size,
historical identities, upstream runtime/HTTP metrics, consumer-added resources/views/meters or
backend retention. This is the last explicitly published observation, not a fresh database read or
proof of persistent installation status. The consumer controls its correctness and freshness.

## Optional OTLP exporter

Install `ServiceMantle.OpenTelemetry.Otlp` to add the official
`OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18.0 exporter without adding that driver to the
core, ASP.NET Core, or base OpenTelemetry packages. Traces and metrics are disabled independently by
default; an all-disabled registration creates no telemetry provider, exporter, authentication
lookup, network connection, or background export activity.

```csharp
serviceMantle.AddOpenTelemetryOtlpExporter(options =>
{
    options.Traces.Enabled = true;
    options.Traces.Protocol = ServiceMantleOtlpProtocol.Grpc;
    options.Traces.Endpoint = new Uri("https://collector.example.com:4317/");
    options.Traces.AuthenticationHeaderName = "primary-otlp";
    options.Traces.ExportTimeout = TimeSpan.FromSeconds(10);
    options.Traces.BatchDelay = TimeSpan.FromSeconds(5);
    options.Traces.MaxQueueSize = 2_048;
    options.Traces.MaxExportBatchSize = 512;

    options.Metrics.Enabled = true;
    options.Metrics.Protocol = ServiceMantleOtlpProtocol.HttpProtobuf;
    options.Metrics.Endpoint = new Uri("https://collector.example.com:4318/v1/metrics");
    options.Metrics.AuthenticationHeaderName = "primary-otlp";
});
```

When authentication is configured, register an
`IServiceMantleOtlpAuthenticationHeaderResolver`. Its non-secret lookup name is stored in options;
the header value is resolved only for an enabled exporter and passed through the official
`OtlpExporterOptions.Headers` entry. ServiceMantle exceptions and option diagnostics do not include
the header value or URI user-info/query components.

Endpoints must be absolute HTTPS URIs. Signal-specific endpoints are passed to the official exporter
as-is; standard OTLP/HTTP collectors commonly use `/v1/traces` and `/v1/metrics`. The
`AllowInsecureLoopbackForTesting` switch permits HTTP only for loopback integration tests and must
not be enabled in deployed configuration. Export timeout is bounded to 1–30 seconds, batch or
collection delay to 100 ms–30 seconds, trace queue size to 100–50,000, and trace batch size to
1–1,000 without exceeding the queue.

ServiceMantle does not add a retry loop. Retry behavior, export failures, batching workers, and host
shutdown belong to the pinned upstream exporter and OpenTelemetry SDK. The bounded queue does not
guarantee delivery, exactly-once export, or loss-free operation during network failures. This package
does not export logs, configure mTLS certificate lifecycles, run a collector, or own the consuming
application's complete telemetry pipeline.

## Authorized Prometheus endpoint

Install the optional `ServiceMantle.OpenTelemetry.Prometheus` package to expose metrics from meters
already selected by the consuming host. The capability is disabled by default and requires an
existing authorization policy when enabled:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("metrics.read", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("scope", "metrics")));

builder.Services
    .AddServiceMantle(
        ServiceId.Parse("catalog"),
        InstanceId.Parse("catalog-01"))
    .AddOpenTelemetryPrometheusEndpoint(options =>
    {
        options.Enabled = true;
        options.AuthorizationPolicyName = "metrics.read";
        options.EndpointPath = "/metrics";
    });

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapServiceMantlePrometheusEndpoint();
```

The endpoint accepts only authorized `GET` and `HEAD` requests on one exact absolute,
single-segment path. It returns the content type produced by the official
`OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.18.0-beta.1 exporter. Scrape responses are limited
to 4 MiB and four concurrent requests; excess work returns an empty `503` response. Request
cancellation and host shutdown are propagated to the response pipeline, and a stopping host rejects
new scrapes. Invalid paths, missing policies, duplicate mappings, route collisions, and conflicting
registrations fail when the host starts.

ServiceMantle disables exporter-generated scope and target-info labels and does not add label mapping
options. It does not add route, service identity, tenant, user, trace/span identity, exception text,
or request values as labels. This boundary does not sanitize or constrain meters, instruments, or
labels registered by the consuming service. The package does not create an authentication scheme or
authorization policy, configure a push gateway, provide storage, alerts, or dashboards, or guarantee
binary compatibility with later prerelease exporter versions.

## Explicit forwarded-header trust

Forwarded headers are disabled unless both registration and middleware insertion are explicit. The
configuration requires at least one trusted proxy address or CIDR and a non-null `ForwardLimit` from
1 through 10:

```csharp
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddForwardedHeaders(options =>
    {
        options.KnownProxies = ["10.0.0.10"];
        options.KnownIPNetworks = ["2001:db8:1234::/64"];
        options.AllowedHosts = ["admin.example.com", "*.internal.example.com"];
        options.ForwardLimit = 2;
    });

var app = builder.Build();
app.UseServiceMantleForwardedHeaders();
```

ServiceMantle creates a private immutable startup snapshot and a dedicated framework
`ForwardedHeadersOptions` instance. It always enables `X-Forwarded-For` and `X-Forwarded-Proto`,
requires header-count symmetry, and enables `X-Forwarded-Host` only when `AllowedHosts` is non-empty.
The framework's implicit loopback trust is removed. Top-level allow-all hosts, ports, invalid or
duplicate normalized values, enumeration failures, and conflicting repeated registrations fail at
application composition or startup without echoing the submitted lists.

Right-to-left chain processing, the first-unknown-hop stop, IPv4-mapped IPv6 handling, host wildcard
and IDN matching, original headers, and truncation use the .NET 10 Forwarded Headers Middleware
semantics. This capability does not configure a proxy, firewall, Host Filtering Middleware, HSTS, or
HTTPS redirection, and it cannot prevent consumers or environment variables from separately enabling
the framework middleware.

## Management paths and startup phase gate

Opt in on the ServiceMantle builder, then put the gate after routing and before endpoint execution:

```csharp
serviceMantle.AddServiceMantlePhaseGate(options =>
{
    options.ManagementPathPrefix = "/management";
    options.SnapshotTimeout = TimeSpan.FromSeconds(1);
});
// Register the consumer's read-only IServiceHealthSnapshotSource when state is available.
var app = builder.Build();
app.UseRouting();
app.UseServiceMantlePhaseGate();
var management = app.MapServiceMantleManagementGroup();
management.MapGet("/status", GetSafeStatus)
    .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Status);
management.MapPost("/bootstrap", ConfigureBootstrap)
    .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Bootstrap);
management.MapPost("/setup", CompleteSetup)
    .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Setup);
management.MapGet("/settings", ReadSettings)
    .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management)
    .RequireServiceMantleManagementAdmin();
```

The handlers are consumer-owned examples, not endpoints supplied by this capability. Authentication,
authorization, rate limiting and response-header policies remain separately required where applicable;
the phase gate does not choose their relative middleware order. Status must be read-only GET/HEAD
and return a safe projection. The gate never bypasses endpoint authorization.

Prefixes are trimmed, lowercased and stripped of one trailing slash. They must be non-root absolute
paths of at most 128 ASCII characters with only letters, digits, `_` and `-` within nonempty segments.
Encoding, dot segments, repeated separators, backslashes, query/fragment syntax and overlap with
`/health` are rejected. Equivalent repeated registrations are idempotent. Invalid/conflicting options,
missing/duplicate middleware use, mismatched surface metadata, unclassified literal management routes,
dynamic first management children and duplicate management route text with overlapping methods fail
at startup. Standard ASP.NET Core owns other route-constraint/custom-matcher ambiguity checks.

| Observed state | Additional surfaces allowed |
| --- | --- |
| BootstrapConfiguration, migration NotStarted or Succeeded | Bootstrap |
| PendingSetup, migration Succeeded, database Reachable | Setup |
| Completed, migration Succeeded, database Reachable | Management and unmarked business endpoints |
| Migration Running/Failed, all other combinations, absent/failed/timed-out source | None |

The library's mapped live/readiness endpoints and read-only Status are available in every state.
Live/Status do not resolve the state source; readiness retains its existing probe behavior. Only
library health metadata enables this exception: arbitrary endpoints with health-looking paths do
not bypass the gate. Bootstrap remains available with an unreachable database so initial
configuration can be supplied. Unmarked routes within the management prefix are denied. Unmatched
requests return 404; phase rejection returns 503 with only `service.phase.unavailable` and no-store.

Each other request obtains one immutable `ServiceHealthSnapshot` from the existing consumer-owned
`IServiceHealthSnapshotSource`. Concurrent phase changes do not rewrite that request's decision or
revoke requests already admitted using an earlier snapshot. Source errors, internal cancellation and
asynchronous timeouts fail closed; request cancellation propagates separately. Timeout is configurable
from 50 ms to 30 seconds and bounds waiting after the source yields, not synchronous blocking or work
that ignores cancellation. This capability does not validate source freshness, run setup/migration,
or govern middleware that short-circuits before the gate. Runtime route reconfiguration is outside
startup validation; per-request path/metadata checks still reject mismatches conservatively.

## Isolated setup and management rate limiting

Rate limiting is opt-in and registers two named sliding-window policies without a global limiter:

```csharp
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddRateLimiting(options =>
    {
        options.Setup.PermitLimit = 5;
        options.Management.PermitLimit = 120;
    });

var app = builder.Build();
app.UseServiceMantleForwardedHeaders(); // when the trusted-proxy capability is configured
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapPost("/setup/complete", CompleteSetup)
    .RequireRateLimiting(ServiceMantleRateLimitingDefaults.SetupPolicyName);
app.MapGet("/management/status", GetManagementStatus)
    .RequireRateLimiting(ServiceMantleRateLimitingDefaults.ManagementPolicyName);
```

`servicemantle.setup` allows 5 requests per minute by default. It partitions by the normalized
`RemoteIpAddress` produced by the configured trusted-proxy boundary, and uses one `unknown-client`
partition when no address is available. It never reads a request Header as an IP fallback.

`servicemantle.management` allows 120 requests per minute by default. A principal with a valid
ServiceMantle management identity is partitioned by a fixed-length SHA-256 projection of its
normalized operator source and ID. An unauthenticated or invalid principal falls back to a separate
trusted-client partition, so management and setup buckets never overlap.

Both policies use six segments by default and always set `QueueLimit` to zero. Permit limits may be
configured from 1 through 60 for setup and 1 through 10,000 for management. Windows may be configured
from 10 seconds through 10 minutes; segment counts may be 1 through 60 and cannot exceed the whole
seconds in the window. Invalid or conflicting repeated registrations fail when the Host starts, while
equivalent repeats are idempotent.

A rejection returns safe `application/problem+json` with status 429 and error code
`rate_limit.exceeded`. `Retry-After` is emitted as rounded-up delta seconds only when the limiter lease
provides that metadata. Rejection bodies, Headers, ServiceMantle diagnostics, and framework metric
tags do not contain the client address, operator identity, partition key, or credentials.

The counters and partition cache belong to the current Host process and are disposed with it. This is
not distributed rate limiting, edge/WAF protection, or a DDoS guarantee; aggregate throughput across
instances is the sum of their independent limits. Endpoints must opt in by policy name, and the
management policy must run after Authentication and before Authorization or endpoint execution.

## Mandatory security response headers

Setup and management API endpoints can opt into an immutable six-header baseline. Registration,
middleware insertion, and endpoint metadata are all explicit:

```csharp
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddSecurityResponseHeaders();

var app = builder.Build();
app.UseServiceMantleSecurityResponseHeaders(); // after routing has selected an endpoint

app.MapPost("/setup/complete", CompleteSetup)
    .RequireServiceMantleSecurityResponseHeaders();
```

While response headers remain unsent, marked endpoints receive exact single values for
`Cache-Control: no-store`, `Pragma: no-cache`, `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action
'none'`. The middleware registers one `OnStarting` callback before invoking downstream code, so
later downstream callbacks run first and ServiceMantle finally collapses each mandatory header.

There are no ServiceMantle options for changing or removing this baseline. Unmarked endpoints and
responses whose headers were already sent are unchanged. The capability does not add HSTS, HTTPS
redirection, CORS, cross-origin isolation headers, TLS configuration, or a policy for HTML UI.

## Request Correlation ID

`UseServiceMantleCorrelationId()` resolves exactly one Correlation ID per request and publishes that
single value to the request context, the response header, and the downstream `ILogger` scope.

```csharp
var app = builder.Build();
app.UseServiceMantleCorrelationId();

app.MapGet("/orders", (HttpContext context) =>
    Results.Ok(context.GetServiceMantleCorrelationId()));
```

The request and response header name is `ServiceMantleHeaderNames.CorrelationId` (`x-correlation-id`)
and the structured field name is `ServiceLogFieldNames.CorrelationId` (`CorrelationId`). The middleware
requires `AddServiceMantle` and throws `InvalidOperationException` at pipeline composition time
otherwise.

A caller value is reused verbatim only when the request carries exactly one header value of 1-64
characters whose first character is an ASCII letter or digit and whose remaining characters are ASCII
letters, digits, `.`, `_`, or `-`. Missing, empty, whitespace, overlong, illegal, comma-joined, and
repeated headers are discarded as a whole and replaced by a newly generated value matching
`^[0-9a-f]{32}$`. Accepted values are never trimmed, normalized, truncated, escaped, or partially
selected, and the original request header is never rewritten.

The header is read once on entry. The resolved value is stored under a private, non-collidable
`HttpContext.Items` key that `GetServiceMantleCorrelationId()` and
`TryGetServiceMantleCorrelationId()` read back, so the accessor, the log scope, and the response
header always agree. A single `Response.OnStarting` callback assigns the response header, collapsing
any downstream values to one. When the response has already started on entry, no callback is
registered and nothing is thrown, but the request context and log scope are still established. The
scope wraps the whole downstream call and is released on success, failure, and cancellation alike; the
middleware logs nothing itself and never swallows a downstream exception or cancellation.

### Explicit non-guarantees

- A Correlation ID is **not** unique, unguessable, or unforgeable, and must never be used for
  authorization, idempotency, replay protection, or audit subject identity. Its generated shape only
  exists to keep log correlation stable.
- It is not propagated to `HttpClient`, message queues, or any other outbound call.
- It is not mapped to W3C `traceparent`, `Activity.TraceId`, or OpenTelemetry.
- No logging sink, Serilog, or console configuration is included.
- Responses produced outside the middleware - Kestrel/transport errors, connection aborts, or a
  response already sent before the middleware ran - are not guaranteed to carry the header.
- Logs written by an outer exception handler are not guaranteed to inherit this scope; only the
  downstream execution and the middleware's own lifetime are covered.
- The original request header is not rewritten, and consumers, downstream middleware, the host, and
  logging providers can still read and record it. The rejected raw value is kept out of the request
  slot, the log scope, the response header, and any diagnostic object this middleware creates - and
  nothing beyond that.
- No other headers or log fields are cleaned; structured value cleaning stays with the existing
  sanitizer.

## Composed HTTP pipeline

`UseServiceMantlePipeline()` fixes the relative order of the existing HTTP capabilities:
configured Forwarded Headers → Correlation ID → Problem Details → Routing → Security Response
Headers → Phase Gate → Authentication (when registered) → Rate Limiting → Authorization (when
registered) → consumer middleware and endpoints.

```csharp
builder.Services.AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddServiceMantlePhaseGate();
// AddForwardedHeaders requires an explicit trusted-proxy configuration when needed.
// Register the consuming service's authentication separately, if used.
var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapGet("/management/status", () => Results.Ok())
    .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Status)
    .RequireServiceMantleSecurityResponseHeaders();
```

Call the composition once, before consumer handlers. Missing required registrations, repeated
composition, or mixing it with individual ServiceMantle middleware on the same builder throws a
fixed configuration exception. Forwarding remains disabled without its explicit trust registration;
the minimal pipeline starts without authentication. The composition does not register a logging
host, health endpoints, telemetry, authentication schemes, or database persistence. Endpoint security
headers, named rate-limit policies, and authorization metadata remain explicit.

Security headers run after routing and before the gate, authentication, and rate limiting, so marked
endpoints receive the existing baseline on 2xx, validation failures, 401/403, 429, exceptions, and
phase rejections while headers remain unsent. Correlation ID wraps exception logging. Forwarding and
authentication run before rate-limit partition selection. The gate retains its existing route table
and precedes authentication; inactive phases can therefore return the established 503 before an
authentication challenge. Exceptions and 429 use Problem Details; phase rejections and management
cookie 401/403 retain their existing safe JSON formats. Arbitrary consumer 4xx responses are not
converted into Problem Details.

Sensitive Header diagnostics still require explicit use of
`ServiceMantleRequestHeaderDiagnosticProjector`; the same immutable registry applies before and
after the gate. No request Header is automatically logged or rewritten. The integration tests lock
the response matrix and demonstrate observable failures for critical inverted orders. The helper
does not inspect arbitrary consumer/framework middleware, prevent an earlier handler from short
circuiting, or extend guarantees to unmarked endpoints, already-started responses, transport errors,
or logs that bypass the projector. Do not independently add routing, authentication, rate limiting,
or authorization around the composed pipeline.

## Safe Problem Details and exception mapping

`UseServiceMantleProblemDetails()` maps downstream exceptions to deterministic RFC 7807 JSON while
the response has not started. Register exact exception types on the `ServiceMantleBuilder`; the
status, title, and stable error code are fixed at registration, and the `type` URI is derived as
`urn:servicemantle:error:{errorCode}`.

```csharp
builder.Services
    .AddServiceMantle(
        ServiceId.Parse("catalog"),
        InstanceId.Parse("catalog-01"))
    .AddExceptionMapping<CatalogRequestException>(
        StatusCodes.Status422UnprocessableEntity,
        "catalog.request_invalid",
        "The catalog request is invalid.",
        new Dictionary<string, Func<CatalogRequestException, object?>>
        {
            ["attempt"] = exception => exception.Attempt,
        });

var app = builder.Build();
app.UseServiceMantleCorrelationId();
app.UseServiceMantleProblemDetails();
```

Every mapped body has `type`, `title`, `status`, `correlationId`, and `errorCode`. The response uses
`application/problem+json`, and its Correlation ID also appears in the `x-correlation-id` response
header and the middleware's safe error log. Place the Correlation ID middleware outside the Problem
Details middleware, as shown, to carry that value through the complete downstream logging scope. If
the Correlation ID middleware is absent, Problem Details generates a safe ID for its response and log.

An exception without an exact mapping - including a subclass of a registered type - always produces
the same environment-independent 500 body with error code `http.internal_server_error`, type
`urn:servicemantle:error:http.internal_server_error`, and title `An unexpected error occurred.`. The
exception message, stack, inner exceptions, and `Data` are never inspected or written by the fallback.
Caller-requested cancellation is propagated while the response has not started; an independently
thrown `OperationCanceledException` is handled like any other unmapped exception.

Custom extension names are the mapping's explicit whitelist. `type`, `title`, `status`, `detail`,
`instance`, `correlationId`, and `errorCode` are protected case-insensitively, so a mapping cannot
replace the fixed status or type or remove the fallback fields. Mapping configuration is validated
when the Host starts. Repeating the same exception mapping is idempotent; conflicting mappings for one
exception type fail startup. If an extension factory or its value cannot be serialized, the complete
response falls back to the generic 500 before any bytes are written.

### Explicit non-guarantees

- The guarantee covers exceptions that pass through this middleware while response headers have not
  been sent. Kestrel/transport errors, connection aborts, process failures, consuming-service
  endpoints outside this pipeline, and separate exception handlers are outside the boundary.
- There is no development-details switch. Development and Production use the same fallback body;
  diagnostics belong in logs linked by the Correlation ID.
- No stable or localized `detail` text is provided. Consumers may depend on `type` and `errorCode`,
  not free text.
- ServiceMantle validates custom extension names but does not sanitize values returned by a consuming
  service's extension factory. The consuming service is responsible for their content and serializer
  behavior.
- When the response has already started, the middleware swallows the downstream exception and leaves
  the already-sent status, headers, and body unchanged.
- The middleware does not implement endpoints, authentication, authorization results, rate limiting,
  or logging sinks. Existing 401, 403, 429, and stage-gate results are unchanged.

## Live and readiness endpoints

The opt-in health capability exposes fixed `GET /health/live`, `GET /health/ready`, and
`GET /health` routes. Live never resolves application state and returns 200 whenever the endpoint can
execute. Ready succeeds only for the finite `Completed + Succeeded + Reachable` combination.

```csharp
builder.Services.AddSingleton<IServiceHealthSnapshotSource, MyHealthSnapshotSource>();
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddServiceMantleHealthEndpoints(options =>
    {
        options.ProbeTimeout = TimeSpan.FromSeconds(3);
        options.ContributorTimeout = TimeSpan.FromSeconds(2);
    })
    .AddServiceReadinessContributor<QueueReadinessContributor>()
    .AddServiceReadinessContributor<DependencyReadinessContributor>();

var app = builder.Build();
app.MapServiceMantleHealthEndpoints();
```

The consumer-owned `IServiceHealthSnapshotSource` returns one immutable `ServiceHealthSnapshot` per
request. ServiceMantle does not infer health from the migration executor and does not run migration,
create a database, write installation state, cache snapshots, or poll in the background. The default
probe timeout is five seconds; valid values are 100 milliseconds through 30 seconds. Internal timeout
and source failure map to `health.probe_timeout` and `health.probe_failed`; caller request cancellation
propagates.

When the base snapshot is ready, registered `IServiceReadinessContributor` implementations receive
that exact immutable snapshot and run sequentially by unique ascending `Order`. Repeating
`AddServiceReadinessContributor<TContributor>()` for the same implementation type is idempotent;
null contributors, unreadable or duplicate orders, and conflicting health registrations fail safely
when the host starts. No contributors is equivalent to approval. Rejections and implementation
failures do not short-circuit later contributors, and the lowest-order failure deterministically wins.
All contributors in one request share one total budget, which defaults to five seconds and accepts
100 milliseconds through 30 seconds. A null result or implementation exception maps to
`health.contributor_failed`; exhausting the total budget maps to `health.contributor_timeout`.
Caller request cancellation remains distinct and propagates its original token.

Ready responses use `application/json` and contain only `status`, `phase`, `migrationStatus`,
`databaseStatus`, and `errorCode`. Probe failures use null state fields and a stable error code. The
source is responsible for read-only, cancellation-aware sampling and for keeping its optional error
code free of connection, exception, SQL, migration-name, or other sensitive values. The result is a
single-process sample, not a freshness, transport-availability, or cross-instance consistency
guarantee.

Contributors are also consumer-owned and must be read-only: ServiceMantle does not prevent a third
party implementation from performing writes or encoding semantically sensitive material in an
otherwise valid error code, and it cannot force a non-cooperative implementation to stop after the
budget token is cancelled. Contributors do not add background polling, caching, persistence, parallel
execution, cross-instance aggregation, or product metrics. They are never invoked for a base
not-ready snapshot, and the live endpoint neither resolves nor invokes them during a request.

## Management identity and authorization

`ServiceMantle` defines a product-agnostic management identity contract, and
`ServiceMantle.AspNetCore` binds it to ASP.NET Core authorization. The contract fixes the claim types,
the first-version permission set, the `ServiceMantle.ManagementAdmin` policy, a three-state identity
provider, and a lossless projection onto the existing `ManagementAuditOperator` model.

| Meaning | Value |
| --- | --- |
| Operator ID claim | `servicemantle.operator_id` |
| Operator source claim | `servicemantle.operator_source` |
| Operator display name claim | `servicemantle.operator_display_name` |
| Permission claim | `servicemantle.permission` |
| `ManagementPermission.Read` | `management.read` |
| `ManagementPermission.Write` | `management.write` |
| `ManagementPermission.Admin` | `management.admin` |
| Helper authentication type | `ServiceMantle.Management` |
| Admin policy name | `ServiceMantle.ManagementAdmin` |

```csharp
builder.Services.AddServiceMantleManagementAuthorization();

app.MapGet("/management/settings", () => Results.Ok())
    .RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
```

A consuming service supplies credentials by implementing one method and calling it through the safe
invoker:

```csharp
public sealed class MyIdentityProvider(IMyCredentialAccessor accessor) : IManagementIdentityProvider
{
    public async ValueTask<ManagementIdentityResult> GetIdentityAsync(CancellationToken token) =>
        await accessor.TryAuthenticateAsync(token) is { } operatorId
            ? ManagementIdentityResult.Authenticated(ManagementIdentity.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId,
                [ManagementPermission.Admin]))
            : ManagementIdentityResult.Unauthenticated();
}

var result = await ManagementIdentityProviderInvoker.InvokeAsync(provider, cancellationToken);
```

Claim parsing is fail-closed and matches both claim type names and claim values as an exact ordinal
wire contract; unknown claim types are ignored. The principal must carry exactly one authenticated
identity, and every ServiceMantle operator or permission claim must live on that identity, so a
synthetic operator can never be assembled from several identities. Exactly one operator ID, exactly
one operator source, zero or one display name, and at least one permission are required; duplicates,
unknown permissions, and values that are not already in their cleaned wire form are rejected. The
operator source is validated by parsing it with `ManagementAuditOperatorSource.TryParse` and then
comparing the raw claim value to the parsed value ordinally, which accepts only an already normalized
wire value without changing that parser's general normalization contract for other callers.

A principal that carries a ServiceMantle operator or permission claim on an identity that is not
authenticated is rejected rather than ignored, so asserting an operator without an authenticated
identity behind it stays distinguishable from simply not being signed in. An unauthenticated identity
that carries no ServiceMantle claim still takes no part in parsing.

`ManagementIdentity.Permissions` and `ManagementPermissions.All` are exposed as `IReadOnlyList<T>` but
backed by a `ReadOnlyCollection<T>`, so neither the permissions of a validated identity nor the fixed
permission set can be changed by casting the declared interface back to an array.

Rejections produced by ServiceMantle itself carry a classification from the closed
`WellKnownManagementIdentityErrorCodes` set, never free text:
`ManagementClaimsParseResult.Invalid` and `ManagementCurrentOperatorResult.ClaimsInvalid` reject any
other value, so no claim value or credential fragment can reach a public `ErrorCode` or `ToString()`.
Use `WellKnownManagementIdentityErrorCodes.IsDefined` to test membership.

`ManagementIdentityResult` is a closed three-state result: `Authenticated`, `Unauthenticated`, and
`Failed(errorCode)`. `Failed` is the one result whose code originates in the consuming service's own
provider, so it enforces the safe 1-64 character ASCII shape rather than the closed set; the provider
is responsible for supplying a classification and never exception text, credentials, or an upstream
response. `ManagementIdentityProviderInvoker` propagates `OperationCanceledException` only
when the caller token has itself requested cancellation; a provider that cancels on its own or throws
anything else yields the stable `management_identity.provider_failed` code, which is the only code
ServiceMantle itself ever puts into a `Failed` result. The current-operator resolver keeps
`Unauthenticated` and `ClaimsInvalid` distinguishable for authorization and auditing.

### Explicit non-guarantees

- No cookie defaults, session results, Data Protection key storage, or login endpoint.
- No local administrator, OIDC login, or break-glass provider, and no default authentication scheme or
  concrete `IManagementIdentityProvider` registration.
- No guarantee of identity-source authenticity: credential validation, signatures, revocation, and
  upstream session lifetime belong to the consuming service's provider or authentication handler.
- No dynamic permission registration, role inheritance, or fine-grained resource authorization.
- No 401/403/503 HTTP body definition; secure session and Problem Details mapping belong downstream.
- No provider timeout, retry, circuit breaking, or exception diagnostics; public results retain only a
  safe classification.
- ServiceMantle cannot constrain what a consuming provider, a custom authentication handler, or a
  logging provider writes on its own. The sensitive-output guarantee covers only the invoker, parser,
  results, exceptions, and authorization components added here.
- A rejection does not hide the fact that an operator lacks the admin permission, but it never carries
  raw claim values, tokens, cookies, credentials, or provider-internal errors.

## Instance-local Bootstrap

The Bootstrap file is an instance-local Secret. It contains the information needed to open and decrypt the business database: the format version, service identifier, database provider, optional server version, connection string, and MasterKey. ServiceMantle does not encrypt this file because the key for doing so is outside this library's responsibility.

By default, the file is stored at:

```text
<AppContext.BaseDirectory>/config/<normalized-service-id>.bootstrap.json
```

The store distinguishes a missing file from a damaged or invalid file. `TryLoad()` returns `null` only when the file is absent; invalid JSON, unknown fields, missing required values, unsupported versions, and service-id mismatches raise `BootstrapException`. `Create()` is for first creation and never overwrites an existing file. `Replace()` atomically replaces an existing file.

The store requires the same `BootstrapDatabaseProviderRegistry` the host validates candidates with,
so construct the provider registry first and hand it to the store. There is no constructor that
skips the registry: a write path that accepted a provider alias but could not resolve it — or that
resolved it through a snapshot other than the one the host actually dispatches with — would put an
unresolvable id on disk.

```csharp
var serviceId = ServiceId.Parse("signacore");
var providerRegistry = new BootstrapDatabaseProviderRegistry([new PostgreSqlBootstrapDatabaseProvider()]);
var store = new BootstrapFileStore(serviceId, providerRegistry);

var bootstrap = new BootstrapConfiguration(
    serviceId,
    new BootstrapDatabaseConfiguration(
        "PostgreSQL",
        "15",
        connectionString),
    masterKey);

store.Create(bootstrap);
var loaded = store.Load();
```

Hosts using `AddServiceMantle` get this wiring automatically: the store is resolved from the
container with the final registry snapshot, so providers added on the returned builder are included.

Bootstrap files belong to individual service instances. Synchronizing or distributing them across multiple instances is not a current ServiceMantle responsibility. When backing up a business database, also back up the matching Bootstrap file for the instance that owns it.

## Bootstrap management use cases

`BootstrapConfigurationManager` is the use-case layer intended for a future management API. Its status projection reports service and instance identity, provider metadata, and whether secret values are configured, but never returns the connection string or MasterKey. Create and update requests are assembled into a complete candidate configuration and must pass an `IBootstrapCandidateValidator` before the local Bootstrap file is written. Updates preserve omitted replacement values and use the existing atomic file replacement semantics.

Bootstrap changes affect only the current instance's local Bootstrap file and return `RestartRequired=true`; the process must be restarted before a change is activated. HTTP endpoints, real database connectivity validation, administrator authentication, and multi-instance synchronization are not implemented yet.

## Installation persistence foundation

`ServiceMantle` core stays provider-agnostic and does not reference EF Core.

`ServiceMantle.Persistence.EntityFrameworkCore` is an optional package that defines:

- `ServiceInstallationEntity` mapping for `service_installations`.
- `IServiceMantleDbContext` contract that business DbContexts implement.
- `ModelBuilder` extension `AddServiceMantleInstallation()` for model registration.
- `EfCoreServiceInstallationStore<TDbContext>` implementing `IServiceInstallationStore`.
- `EfCoreServiceSetupCodeStore<TDbContext>` implementing `IServiceSetupCodeStore`.
- `AddServiceMantleDataProtectionKeys()` and `EfCoreDataProtectionKeyRepository<TDbContext>` for
  encrypted, service-isolated ASP.NET Core Data Protection key rings.
- `EfCoreServiceSettingStore<TDbContext>` implementing `IServiceSettingStore` with one dedicated
  DbContext and explicit transaction per update.

Installation-state reads propagate caller cancellation as `OperationCanceledException`. Connection,
command, and provider failures are normalized to `ServiceInstallationStoreException` with
`installation.storage_error`; its public message and `ToString()` contain only the stable
classification and safe text. `InnerException` may retain provider diagnostics and must only be
inspected at a controlled diagnostic boundary, never written directly to untrusted output.

This layer standardizes installation state, shared configuration, and encrypted Data Protection key
persistence; it does not generate migrations, execute `Database.MigrateAsync`, or assume ownership
of Bootstrap secrets. `service_installations`, `service_settings`, and
`service_data_protection_keys` rows are owned by the consuming service database.

`AddServiceMantleSettings()` maps one `service_settings` aggregate row per `service_id`. The row stores
the complete raw value set, one monotonic service-level concurrency version, the UTC update time,
caller-supplied operator identifier, and restart marker. `EfCoreServiceSettingStore<TDbContext>` uses
an `IDbContextFactory<TDbContext>` so its explicit transaction and single `SaveChangesAsync` call
cannot commit a consumer shared work unit. A batch with a stale expected version fails as
`service_settings.version_conflict` without merging, retrying, or partially writing values. Product
types, defaults, required values, sensitivity, and composite constraints remain the responsibility
of `ServiceSettingDefinitionRegistry`; this persistence layer stores the caller-supplied raw form.
Read failures expose only a stable `ServiceSettingStoreException` classification and safe message;
the exception never retains provider diagnostics, connection strings, credentials, or setting values.

## Transactional setting batches

`ServiceSettingUpdateService` validates the complete candidate, encrypts changed sensitive values,
and writes one key-only audit per changed key through `IServiceSettingUpdateTransaction`.
`ServiceSettingUpdateCommand` contains one to 32 changes and an expected service version; null removes
an explicit value so catalog defaults apply. Unknown input keys are not echoed in errors. Registered
keys, validation codes and operator identifiers must be non-secret product metadata. Raw commands,
root keys, decrypted candidates and EF sensitive-data logging must stay out of logs and responses.

For EF Core, map both `AddServiceMantleSettings()` and `AddServiceMantleManagementAudit(dialect)` in the
consumer's context. Register its existing scoped context, `ServiceId`, the definition registry and,
for sensitive settings, `IServiceSettingRootKeySource`, then add:

```csharp
services.AddScoped<IServiceSettingUpdateTransaction,
    EfCoreServiceSettingUpdateTransaction<ApplicationDbContext>>();
services.AddScoped<ServiceSettingUpdateService>();

await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
var result = await updateService.UpdateAsync(
    new ServiceSettingUpdateCommand(expectedVersion,
        new Dictionary<string, string?> { ["smtp.password"] = newPassword },
        validatedOperator), cancellationToken);
if (!result.Succeeded)
{
    await transaction.RollbackAsync(CancellationToken.None);
    return result;
}
await transaction.CommitAsync(cancellationToken);
return result;
```

The adapter requires an existing transaction with savepoint support and a context without pending
changes or tracked setting aggregates. It explicitly saves only the batch and its audits, including
when automatic change detection is disabled, but never commits the caller's outer transaction.
`Applied` means saved within that transaction. Caller rollback removes the batch and audits together.
Save failures or cancellation restore the savepoint and detach only entries owned by this operation;
if the connection or rollback fails, discard the whole outer transaction. A failed outer commit has
its normal provider-specific uncertainty; the result does not prove a commit occurred. SQL Server
MARS transactions without savepoint support are rejected.

`VersionConflict` covers stale versions and detected concurrent inserts/updates; retry only after
rolling back, opening a fresh transaction and reloading. No automatic merge or retry is performed.
Caller cancellation throws a safe `OperationCanceledException`; other failures return closed status
values without underlying exceptions or setting values. Validators and root-key sources remain
trusted product code with no execution-time or memory bound. This entry point does not authorize
operators or activate runtime snapshots. The existing raw `IServiceSettingStore` keeps its separate
persistence contract. Do not use one update service/context concurrently.

## Typed setting snapshots

`AddServiceMantleSettingSnapshots()` registers the provider-independent snapshot loader, immutable
definition registry, store adapter, process-local current-snapshot accessor, and safe read-only query
service. Register product definitions and an `IServiceSettingStore`; catalogs containing sensitive
definitions must also
register an `IServiceSettingRootKeySource` backed by the instance's Bootstrap root key.

```csharp
services.AddSingleton<IServiceSettingDefinitionProvider, ProductSettingDefinitions>();
services.AddSingleton<IServiceSettingStore, ProductSettingStore>();
services.AddSingleton<IServiceSettingRootKeySource, BootstrapRootKeySource>();
services.AddServiceMantleSettingSnapshots();

var result = await serviceProvider
    .GetRequiredService<ServiceSettingSnapshotLoader>()
    .RefreshAsync(cancellationToken);

if (result.Succeeded &&
    serviceProvider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>()
        .TryGetCurrent(out var snapshot))
{
    var enabled = snapshot!.Values["product.enabled"].GetBoolean();
}

var queries = serviceProvider.GetRequiredService<ServiceSettingQueryService>();
var definitions = queries.GetDefinitions();
var current = await queries.GetCurrentAsync(cancellationToken);
if (current.Succeeded)
{
    var enabled = current.Values.Single(value => value.Key == "product.enabled");
    Console.WriteLine($"Version {current.Version}: {enabled.Value} ({enabled.Source})");
}
```

Every refresh reads one complete service-level version, verifies registered keys and persisted
types, decrypts sensitive values only from `sm:v1:` envelopes using the stable setting key as the
protection purpose, performs deterministic type conversion, and runs the existing single-value and
composite validation rules. Refreshes on one loader are serialized. A successful newer version is
published by one atomic reference replacement; readers therefore observe either the previous
complete snapshot or the new complete snapshot. A failed or cancelled refresh preserves the exact
previous reference. Older versions fail with `configuration.snapshot_stale`; equal normalized
content is idempotent, while different content at the same version fails with
`configuration.snapshot_conflict`.

The read-only query service is independent of HTTP and authorization. Definition projections expose
only `Key`, `ValueType`, `IsRequired`, `IsSensitive`, `HasDefault`, and `RequiresRestart`; they do not
expose default material or constraint implementations. Each current-value query performs exactly one
complete refresh and projects only that refresh's successful snapshot version. Non-sensitive strings,
numbers, Booleans, and JSON use stable invariant text, while sensitive values always have a null
projected value and expose only whether persisted material is present. Failed refreshes return the
existing closed snapshot error classifications with no partial or previous values. The query service
does not update settings, authorize callers, add caching or background refresh, or bypass the complete
snapshot validation path.

This atomicity is process-local. The source must provide a complete read and is responsible for any
storage snapshot isolation. The loader does not write settings, assign versions, rotate keys, poll
for changes, coordinate publication across processes, or accept unknown future envelope formats.

Management consumers should implement a minimal integration model, for example:

```csharp
public sealed class MyDbContext : DbContext, IServiceMantleDbContext
{
    public DbSet<ServiceInstallationEntity> ServiceInstallations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleInstallation();
    }
}
```

`service_data_protection_keys` uses `(service_id, key_id)` as its identity, where `key_id` is the
standard Data Protection repository name (`key-*` or `revocation-*`). Key and revocation XML are both
stored only as authenticated `sm:v1:` envelopes in `encrypted_xml`. The repository derives a distinct
encryption context for each service and repository element through `SensitiveValueProtector`; a wrong
Bootstrap root key, damaged ciphertext, or ciphertext copied to another service fails closed. Each
call creates its own DbContext, and each insert owns an explicit transaction, so it cannot commit a
consumer's shared work unit. Register the consuming context as an
`IDbContextFactory<TDbContext>`, add the mapping, and connect Data Protection through the package
extension:

```csharp
services.AddDbContextFactory<MyDbContext>(options => /* provider configuration */);
services.AddDataProtection()
    .PersistKeysToServiceMantleEfCore<MyDbContext>(
        ServiceId.Parse("orders-api"),
        serviceProvider => serviceProvider
            .GetRequiredService<MyBootstrapState>()
            .DataProtectionRootKey);

// MyDbContext.OnModelCreating:
modelBuilder.AddServiceMantleDataProtectionKeys();
```

ServiceMantle does not generate migrations or manage, distribute, or rotate the external root key.
Those remain consumer and deployment responsibilities. ASP.NET Core Data Protection continues to own
its key lifecycle, including individual and mass revocation records.

`service_installations`, `service_settings`, `service_data_protection_keys`, and
`service_audit_logs` (see below) are defined. Planned future tables (not in this release):

- administrator identity/state tables

Shared migration ownership is intentionally moved to the consuming service. In deployments against existing business databases, services must create migration entries and keep installation table ownership in their own startup/deployment process.

`IServiceInstallationStore.CreatePendingAsync` is a standalone write: when the row is absent it calls
`SaveChangesAsync` once, never creates or commits a transaction, and refuses to run when the DbContext
already carries any `Added`, `Modified`, or `Deleted` entry. That refusal raises
`ServiceInstallationStoreException` with `installation.dirty_context`; unrelated `Unchanged` entries
are allowed. The precondition explicitly detects changes, including when automatic change detection
is disabled. Use a short-lived dedicated DbContext. If the call joins an external transaction,
success does not guarantee that the caller will commit it.

Concurrent insertion remains idempotent only when EF Core identifies the failed entry as the
`service_installations` insert and a follow-up read finds the competing row. Other update failures use
the safe `installation.storage_error` exception channel even if a row happens to exist; classification
does not depend on provider-specific error numbers.

## One-time Setup Code

A pending installation carries a one-time Setup Code as attached state on the same
`service_installations` row. `service_installations.Status` stays the only durable authority for
whether an installation is complete; no second Pending/Completed source is introduced.

The row gains a non-negative 32-bit `SetupCodeGeneration` counter plus nullable
`SetupCodeDigest`, `SetupCodeIssuedAtUtc`, and `SetupCodeExpiresAtUtc` columns. Generation 0 with all
three nullable fields empty means never issued; a positive generation with complete, well-ordered
material means issued. Every other combination is `setup_code.storage_corrupt` and fails closed. The
generation counter is what makes a fresh pending row distinguishable from a row whose material was
deleted after being issued - without it, both would present as one set of nulls - so it is retained
after completion as a non-sensitive issuance history marker.

A code is 24 bytes (192 bit) of cryptographically secure randomness rendered as unpadded Base64URL:
exactly 32 case-sensitive `[A-Za-z0-9_-]` characters. Only the digest is stored, in the fixed
`sha256-v1:` plus 64 lowercase hexadecimal format, computed over the exact UTF-8 bytes of the code and
compared in constant time. An unknown digest version or a malformed stored digest is storage
corruption, never an ordinary invalid code. The default lifetime is 30 minutes and the configurable
range is the closed interval from 5 minutes to 24 hours.

| Current state | Operation | Result |
| --- | --- | --- |
| Pending, never issued | Create | generation 1, plaintext returned by this result only |
| Pending, already issued | Create | `setup_code.already_exists` |
| Pending, issued, generation < `int.MaxValue` | Rotate | generation + 1, material replaced atomically |
| Pending, issued, generation == `int.MaxValue` | Rotate | `setup_code.generation_exhausted` |
| Pending, never issued | Rotate | `setup_code.not_created` |
| Pending, corrupt material | any | `setup_code.storage_corrupt` |
| Pending, valid code | Validate | valid, read-only |
| Pending, malformed or mismatched candidate | Validate | `setup_code.invalid` |
| Pending, expired material | Validate / StageConsume | `setup_code.expired` |
| Pending, valid code | StageConsume | material cleared, Completed staged, not saved |
| Completed | any | `installation.completed` |

Except for programming errors and caller cancellation, every operation decides in this fixed order:
DbContext ownership precondition (`installation.dirty_context`), `installation.not_found`,
`installation.state_invariant_violation`, `installation.completed`, `setup_code.storage_corrupt`,
`setup_code.generation_exhausted`, candidate format (`setup_code.invalid`), expiry
(`setup_code.expired`), and finally the digest comparison (`setup_code.invalid`). Deciding expiry
before the digest comparison keeps expired material a stable `expired` answer even when the candidate
does not match, and a completed installation is never mis-reported as an invalid code because the
caller submitted a malformed one.

Every rejection carries a classification from the closed `WellKnownSetupCodeErrorCodes` set, never
free text: `SetupCodeIssueResult.Rejected`, `SetupCodeValidationResult.Rejected`, and
`SetupCodeConsumptionResult.Rejected` reject any other value. A Setup Code is 32 unpadded Base64URL
characters, so a character-shape rule alone would accept a candidate verbatim and publish it through
`ErrorCode` and `ToString()`. Use `WellKnownSetupCodeErrorCodes.IsDefined` to test membership. A
corrupt base installation row is projected onto the declared
`installation.state_invariant_violation` classification, so an unreadable row never breaks the closed
result contract either.

Reads are part of that contract. A connection, command, or provider failure while loading the
installation row raises `ServiceInstallationStoreException` with the stable
`installation.storage_error` code and a message that carries no provider detail; it is never
downgraded to a candidate rejection. Caller cancellation still propagates as
`OperationCanceledException`.

```csharp
var setupCodeStore = new EfCoreServiceSetupCodeStore<MyDbContext>(dbContext);

var issued = await setupCodeStore.CreateAsync(serviceId, cancellationToken);
if (issued.IsIssued)
{
    // The only moment the plaintext exists outside the operator's hands.
    ShowOnce(issued.SetupCode!.Reveal());
}
```

Create and Rotate are standalone writes: they call `SaveChangesAsync` once, never create, commit, or
take over a database transaction, and refuse to run at all when the DbContext already carries any
`Added`, `Modified`, or `Deleted` entry (`installation.dirty_context`). Unrelated `Unchanged` entries
are fine. The precondition detects changes explicitly before reading entry states, so it still holds
when the caller has set `ChangeTracker.AutoDetectChangesEnabled` to false; that setting is read, never
changed. A short-lived dedicated DbContext is the recommended shape. The plaintext is only built into
a result after that save has succeeded; when the caller wraps the call in a larger external
transaction, success only means the save joined that transaction, and after an external rollback the
plaintext simply no longer validates.

`StageConsumeAsync` stages the material clearing, the Completed status, the completion timestamp, and
the version increment without saving, so the caller commits it together with its own work:

```csharp
await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

var consumption = await setupCodeStore.StageConsumeAsync(serviceId, candidate, cancellationToken);
if (!consumption.IsStaged)
{
    return consumption.ErrorCode;
}

await contributors.StageAsync(dbContext, cancellationToken);
await dbContext.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

It tolerates unrelated dirty entries so it can join a larger unit of work, but the target installation
entry must be untracked or `Unchanged` beforehand; an already `Added`, `Modified`, or `Deleted` target
returns `installation.dirty_context` rather than validating against uncommitted caller values.

A database rollback restores only the database. It does **not** restore the Completed status and
current values already staged in the EF Core change tracker. After a rollback the caller must dispose
that DbContext, or explicitly reload, restore, or detach the installation entry, before reusing it -
never retry on, or read installation authority from, an unrestored tracker.

`IServiceInstallationStore.MarkCompletedAsync` is retained so that no unrelated public API is removed,
but its behaviour is fixed: a pending row stably raises `ServiceInstallationStoreException` with
`installation.setup_code_required`, and an already completed row stays an idempotent read. Every path
that completes a pending installation now goes through `StageConsumeAsync`.

### Explicit non-guarantees

- No contributor ordering or execution, HTTP endpoint, or rate limiting. Real PostgreSQL contention
  coverage in this release is limited to concurrent creation of the pending installation row; Setup
  Code issue, rotation, and consumption have no real multi-instance PostgreSQL contention proof.
- No decision about whether a brand-new database or an upgraded existing database may create the
  initial pending row; that belongs to the consuming migration or adoption work.
- No anonymous rotate or repair channel. Corrupt pending material fails closed and is only resolvable
  by an explicit operational repair or migration.
- No guarantee that a caller who received a plaintext once will not copy or log it. The guarantee is
  that ServiceMantle's own persistence, exceptions, log value projections, and diagnostics never echo
  it.
- The SHA-256 scheme relies on the 192-bit high-entropy random code. It is not for user passwords and
  claims no confidentiality once both the database and process memory are compromised.
- No guarantee that a caller's external transaction is ultimately committed; ServiceMantle never takes
  that transaction over.
- No guarantee that an external rollback restores the in-memory tracking state of the same DbContext.

## Management audit persistence

`ServiceMantle.Audit` (in the core `ServiceMantle` package) defines a product-agnostic management audit domain: `ManagementAuditEvent` (write contract input), `ManagementAuditRecord` (read model), `ManagementAuditQuery`/`ManagementAuditQueryResult` (query contract), and bounded value types `ManagementAuditAction`, `ManagementAuditTargetType`, `ManagementAuditTarget`, `ManagementAuditOperator`, `ManagementAuditOperatorSource`, and the `ManagementAuditOutcome` security result enum (`Unknown`/`Success`/`Failure`/`Denied`). `WellKnownManagementAuditActions`, `WellKnownManagementAuditTargetTypes`, and `WellKnownManagementAuditOperatorSources` provide reusable conventions for installation, administrator login, and configuration-change events; consuming services define additional actions and target types with the same `Parse` pattern (for example `signacore.account_created`). ServiceMantle does not define SignaCore-specific identity, application, credential, signing-key, or OAuth semantics.

`ManagementAuditEvent.Create(...)` enforces the sensitive-content policy before an event can be constructed: metadata keys must normalize to ASCII under NFKC so mixed-script confusables cannot hide a sensitive name; keys that name a secret (`password`, `passwd`, `passphrase`, `accountkey`, `privatekey`, `token`, `connectionstring`, `apikey`, `setupcode`, `authorization`, and similar) are rejected outright. When a description, display name, or metadata value contains a recognized secret assignment or database/credential-bearing URI, the entire free-text field is replaced with `[REDACTED]` so punctuation or opaque quoting cannot expose a suffix; bearer tokens, JWT-like strings, PEM private key blocks, and recognized connection strings are also redacted. Client IPs and correlation IDs use strict format allowlists; opaque operator and target identifiers that contain a supported secret-shaped format are rejected because modifying them would destroy identity semantics.

This sanitization is a defense-in-depth contract for the formats listed above, not a general-purpose data-loss-prevention engine: an opaque bare value has no intrinsic signal that distinguishes a secret from ordinary audit text. Callers **must not** place connection strings, external root keys, database administrator credentials, setup codes, passwords, tokens, or other sensitive configuration values in any audit field. Consumption-specific metadata should use an explicit non-secret allowlist before calling ServiceMantle. The persistence write guarantee applies to records staged through `EfCoreManagementAuditWriter<TDbContext>`: the writer reapplies the supported-format policy before the caller saves the shared unit of work. The mapped audit entity is internal and no writable audit `DbSet` is exposed by the package. Direct SQL, imports, and administrative database writes are outside that write guarantee; the query boundary still revalidates such legacy rows so recognized sensitive content is not returned unchanged.

`ServiceMantle.Persistence.EntityFrameworkCore` adds:

- Internal entity mapping for `service_audit_logs`; consumers do not expose a writable audit `DbSet`.
- `ModelBuilder` extension `AddServiceMantleManagementAudit(...)` for model registration. Pass the
  consuming database's `ManagementAuditDatabaseDialect` so every persisted text column is bounded by the provider's encoded-byte function and the generated constraints use valid SQL. Query pages preflight the same resource ceilings before EF materializes text, while domain validation continues to enforce the exact character and format limits.
- `EfCoreManagementAuditWriter<TDbContext>` implementing `IManagementAuditWriter`. It only stages the internal entity on the caller's configured `DbContext` — it never calls `SaveChangesAsync` and never commits a transaction. The write participates in whatever unit of work or explicit transaction the caller already owns, and future Setup/configuration flows can call it before their own `SaveChangesAsync` to persist an audit record atomically with their own changes.
- `EfCoreManagementAuditQueryService<TDbContext>` implementing `IManagementAuditQueryService`, providing bounded keyset-paginated queries filtered by action, target, operator, and time range. The first result returns an opaque `ContinuationCursor`; pass it unchanged to the immediately following `ManagementAuditQuery` rather than using an unbounded offset. The cursor is bound to the normalized filters, sort order, page size, and next page number, so it cannot be silently reused with a different query.

`TotalCount` is the count observed while each query executes and may change when rows are inserted or deleted concurrently. Continuations have ordinary keyset semantics: they avoid offset drift and repeated rows already passed in the ordering, but they do not represent a database snapshot. A concurrently inserted backfilled record whose ordering key lies after the cursor can therefore appear on a later page.

```csharp
public sealed class MyDbContext : DbContext, IServiceMantleDbContext
{
    public DbSet<ServiceInstallationEntity> ServiceInstallations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddServiceMantleInstallation();
        modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.PostgreSql);
    }
}

var writer = new EfCoreManagementAuditWriter<MyDbContext>(dbContext);
var auditEvent = ManagementAuditEvent.Create(
    ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, operatorId: "admin-1"),
    WellKnownManagementAuditActions.ConfigurationChanged,
    ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
    outcome: ManagementAuditOutcome.Success);

await writer.RecordAsync(auditEvent);
// ... stage other business changes on the same dbContext ...
await dbContext.SaveChangesAsync(); // caller owns save/commit
```

Authentication, admin cookies, log storage/shipping, an HTTP API surface, Setup, and shared configuration are out of scope for this layer; `service_audit_logs` rows are owned by the consuming service database, the same as `service_installations`.

## Provider SPI and validation dispatch

The core library now provides a provider SPI so validation can be extended without changing the core package.

- `IBootstrapDatabaseProvider` implementations are expected in optional provider packages and receive only `BootstrapDatabaseConfiguration`.
- `BootstrapDatabaseProviderRegistry` resolves providers by canonical `Provider` id and aliases using case-insensitive matching.
- `BootstrapDatabaseCandidateValidator` performs generic checks (`database.provider_not_registered`, server-version constraints) and then dispatches to the matched provider, always passing the provider its own canonical id.
- Provider IDs in bootstrap files are validated for safe syntax and canonicalized through the shared resolver; driver-specific behavior is handled by the candidate validator at management time.
- Driver packages are distributed separately, so the ServiceMantle core package stays free of database driver dependencies.

### Canonical provider ids

`BootstrapDatabaseProviderDescriptor.Aliases` is public, so a provider id that reaches the bootstrap
file may be an alias rather than the descriptor's canonical `Id`. One immutable snapshot,
`DatabaseProviderIdResolver`, is the single place that alias rule lives:
`BootstrapDatabaseProviderRegistry` builds it and exposes it as `ProviderIdResolver`.
`BootstrapFileStore` takes the registry itself and canonicalizes through that registry's snapshot;
`DatabaseTargetPreparationProviderRegistry` and `DatabaseMigrationLockProviderRegistry` take that
same snapshot. None of them copies descriptor alias enumeration. Without it, an alias could be persisted, read back verbatim, and then miss the
migration lock registry — producing `migration.lock_not_supported` with no write-time warning.

Guaranteed:

- A registered canonical id or alias, in any casing, resolves to the descriptor's exact `Id`.
  Matching is `OrdinalIgnoreCase`; the output casing is always the descriptor's.
- `BootstrapFileStore.Create`/`Replace` canonicalize before serializing, so a registered alias never
  reaches disk. `Load`/`TryLoad` canonicalize after deserializing, so an alias already on disk is
  canonical to every caller.
- The candidate provider, the target preparation lookup, and the migration lock lookup all see the
  same canonical id, and the keyed registries canonicalize both their registration keys and their
  lookup keys.
- Registration conflicts (a duplicate canonical id, an alias colliding with another alias or with any
  canonical id, in either registration order) are rejected when the snapshot is built.
- A snapshot is fixed at construction; one store and its related keyed registries share one snapshot.

Not guaranteed:

- Bytes on disk are only guaranteed for files written through the store. Hand-edited files, files
  produced by a deployment system, and direct writes are unaffected — though a resolver-aware store
  still canonicalizes registered aliases when reading them.
- Reading never rewrites the file. An existing alias is canonicalized in memory only and becomes
  canonical on disk at the next `Replace()`.
- A string that is syntactically valid but absent from the snapshot cannot be classified as an alias
  or a canonical id, so it is preserved as the caller declared it. The resolver never guesses at
  aliases nobody declared. This keeps the existing third-party and deployment flow where the file is
  written before the provider is registered.
- Resolving an alias never implies a capability. A registered bootstrap provider does not mean a
  target preparation provider or a migration lock provider exists; those still fail closed with
  `database_target_preparation.capability_not_supported` and `migration.lock_not_supported`.
- Alias priority, last-registration-wins, and case-ambiguity rules are not introduced.

Current and planned provider packages are:

- `ServiceMantle.Database.PostgreSql` validates PostgreSQL settings, performs a minimum read probe (`SELECT 1`) against the target database, and provides session-level advisory lock capability for multi-instance migration coordination (implementation complete, pending CI container verification).
- `ServiceMantle.Persistence.EntityFrameworkCore` provides shared install-state persistence and consumption patterns.
- `ServiceMantle.Database.Sqlite` is a referenceable package skeleton; SQLite target preparation and
  migration integration remain separate follow-up capabilities.
- `ServiceMantle.Database.MySql` validates MySQL 8+ settings, observes server-database targets,
  and explicitly creates a missing target without changing an existing database.
- `ServiceMantle.Database.MariaDb` validates MariaDB 10.11+ settings and server identity, observes
  server-database targets, explicitly creates a missing target without changing an existing database,
  and provides a dedicated-session `GET_LOCK` migration lease.
- `ServiceMantle.Database.Oracle` validates Oracle 19c+ single-instance PDB password-user
  settings, observes a local user and its same-named schema, and can explicitly create a missing
  user with only `CREATE SESSION` while preserving every pre-existing user. It also provides an
  exclusive `SYS.DBMS_LOCK` migration lease on a dedicated target-user session.
- `ServiceMantle.Database.SqlServer` validates SQL Server 2019+ settings, observes server-database
  targets, explicitly creates a missing target without changing an existing database, and provides
  a session-owned `sp_getapplock` migration lease.

The PostgreSQL provider validates configuration and target connectivity. It also provides session-level advisory lock capability for safe multi-instance migration coordination.

MySQL and MariaDB keep independent provider IDs even if they can share lower-level behavior.
Oracle uses a `ServerSchema` target: the canonical local PDB user and its same-named schema are one
identity. Preparation requires an unpooled administrator connection to the same `Data Source` and
direct `CREATE USER`, `DROP USER`, and `CREATE SESSION WITH ADMIN OPTION` privileges. It does not
grant quota or schema-object DDL privileges, repair an existing account, or support RAC, cloud,
root/common users, wallets, tokens, external authentication, proxy authentication, or non-CDB
deployments. SQL Server and SQLite follow their own target semantics.

Oracle connection-string syntax errors and rejected attributes (including authentication attributes
not recognized by the pinned ODP.NET version) fail before connecting. Bootstrap returns
`database.connection_string_invalid`; observation and preparation return
`database_target_preparation.invalid_target`, including invalid administrative connection strings.
Parser exceptions and their potentially sensitive messages are not included in these results.

Oracle migration locking is a separately registered capability. The target user needs a direct
`EXECUTE ON SYS.DBMS_LOCK` grant; target preparation deliberately does not grant it. The provider
uses an unpooled, non-enlisted target-user session and never commits the consumer's work unit.

## Database target preparation

Database target preparation is a separate, optional capability from bootstrap validation. A provider that implements `IBootstrapDatabaseProvider` does not automatically support preparing (creating) a missing target; a provider opts in only by also registering an `IDatabaseTargetPreparationProvider` implementation. Callers resolve this capability through `DatabaseTargetPreparationProviderRegistry` and must fail closed with `database_target_preparation.capability_not_supported` when no preparation provider is registered for a database provider id, rather than treating an unsupported provider as already prepared.

ASP.NET Core hosts register the optional capability explicitly on the builder returned by
`AddServiceMantle`; the container-provided registry shares the Bootstrap store's provider-id resolver
snapshot. Registering only the Bootstrap provider does not add target preparation support.

```csharp
services
    .AddServiceMantle(serviceId, instanceId)
    .AddBootstrapDatabaseProvider<PostgreSqlBootstrapDatabaseProvider>()
    .AddDatabaseTargetPreparationProvider<PostgreSqlDatabaseTargetPreparationProvider>();

var preparationProviders = serviceProvider
    .GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
```

The capability models three target kinds via the existing `BootstrapDatabaseTargetKind` enum (`ServerDatabase`, `File`, `ServerSchema`), and exposes three independent observation signals:

- **Server reachable** (`DatabaseTargetObservation.IsServerReachable`) — the database server responded to the connection attempt.
- **Target exists** (`DatabaseTargetObservation.TargetExists`) — `true` when existence was proved, `false` when absence was proved, and `null` when the connection failed before existence could be established.
- **Target connectable** (`DatabaseTargetObservation.IsTargetConnectable`) — a connection to the target itself succeeded.

```csharp
var preparationProviders = new DatabaseTargetPreparationProviderRegistry(
    [new PostgreSqlDatabaseTargetPreparationProvider()],
    providerRegistry.ProviderIdResolver);

if (!preparationProviders.TryGetProvider(bootstrapDatabaseConfiguration.Provider, out var provider))
{
    // Fail closed: this provider does not support target preparation.
    return;
}

var observation = await provider!.ObserveAsync(bootstrapDatabaseConfiguration, cancellationToken);
if (observation.IsTargetConnectable)
{
    return; // Already ready; nothing to prepare.
}

if (observation.TargetExists is not false)
{
    // Authentication failed, existence is unknown, or an existing target is not connectable.
    // Preserve the observation failure instead of turning it into a false AlreadyExists success.
    logger.LogError("Target is not connectable: {ErrorCode}", observation.ErrorCode);
    return;
}

// AdministrativeConnectionString is used only for the duration of this call. It is never
// persisted, logged, included in diagnostics, or returned in any result.
var request = new DatabaseTargetPreparationRequest(bootstrapDatabaseConfiguration, administrativeConnectionString);
var result = await provider.PrepareAsync(request, timeout: TimeSpan.FromSeconds(30), cancellationToken);

if (!result.Succeeded)
{
    logger.LogError("Target preparation failed: {ErrorCode}", result.ErrorCode);
    return;
}

var preparedObservation = await provider.ObserveAsync(bootstrapDatabaseConfiguration, cancellationToken);
if (!preparedObservation.IsTargetConnectable)
{
    logger.LogError("Prepared target is not connectable: {ErrorCode}", preparedObservation.ErrorCode);
}
```

`DatabaseTargetPreparationResult.Outcome` reports `Created` or `AlreadyExists`. Implementations must never overwrite, drop, recreate, or otherwise destructively modify a target that already exists.

### Server identity before creation

PostgreSQL, MySQL, MariaDB, and SQL Server now require a live same-server proof before
accepting or creating a database. A temporary random session-lock challenge on the
administrative connection must be visible through the target endpoint and credentials.
Creation then uses that same administrative session. Host names and connection strings need
not match, so legitimate aliases are supported.

The target login must be able to access the provider's maintenance namespace: the selected
administrative database for PostgreSQL (default `postgres`), no selected database for
MySQL/MariaDB, or `master` for SQL Server. A missing target does not prevent proof. Inability
to authenticate or read the proof fails without creation; a negative proof returns
`database_target_preparation.invalid_target`. This tightens earlier preparation behavior:
invalid target credentials can no longer be bypassed using the administrator's credentials.

Use authenticated stable single-server routes independent of database/user selection.
Unknown proxy routing, session migration, transparent failover, and read-only routing are
outside the supported boundary. See the [server identity contract](docs/contracts/server-database-identity.md)
for evidence, privileges, cleanup, error mappings, and precise non-guarantees.

### PostgreSQL target preparation

`ServiceMantle.Database.PostgreSql.PostgreSqlDatabaseTargetPreparationProvider` observes a PostgreSQL target with a single connection attempt. A structured "database does not exist" response (SQLSTATE `3D000`) proves the server is reachable and the target is missing. Authentication errors can occur before PostgreSQL checks the database name, so those observations report a reachable server with `TargetExists == null`; target-level `CONNECT` denial (`42501`) reports a known existing but unreachable target. `PrepareAsync` uses the caller-supplied administrative connection string with pooling forcibly disabled and outside any ambient transaction to check `pg_database` and, only when the target is absent, issue `CREATE DATABASE ... OWNER ...`; the owner is the target connection string's PostgreSQL username and must already exist as a role.

Before creating anything, preparation verifies that the requested database and owner names are actually reachable through Npgsql after creation: Npgsql writes startup-packet identifiers as UTF-8 and PostgreSQL silently truncates them at its 63-byte identifier limit while storing them converted into `server_encoding`, so names are accepted only when their UTF-8 form fits 63 bytes and is byte-identical to their stored form — meaning servers whose `server_encoding` differs from UTF-8 accept pure-ASCII names only. This rejects, before any side effect, names that would otherwise create a database the application can never connect to (for example non-ASCII names on LATIN1 servers). An existing database with the same name is reported as `AlreadyExists` only when it is owned by the target username; a differently-owned database — pre-existing or created concurrently in a race observed as either the `duplicate_database` error (`42P04`) or a unique-key violation on the `pg_database` name index (`23505`) — fails closed with `database_target_preparation.target_conflict` instead of pretending the target is ready.

### MySQL target preparation

`ServiceMantle.Database.MySql` keeps the canonical `MySQL` provider identity independent from
`MariaDB`. Hosts opt in explicitly:

```csharp
services
    .AddServiceMantle(serviceId, instanceId)
    .AddBootstrapDatabaseProvider<MySqlBootstrapDatabaseProvider>()
    .AddDatabaseTargetPreparationProvider<MySqlDatabaseTargetPreparationProvider>();
```

The provider accepts numeric MySQL 8+ server versions and database identifiers that fit MySQL's
64-character limit without control characters, NUL, surrogate code units, or a trailing ASCII
space. `ObserveAsync` only attempts a target connection and never invokes creation. `PrepareAsync`
uses the caller's administrative connection only for that call, clears its database, disables
pooling and ambient transaction enlistment, and creates a missing database with `utf8mb4` and
`utf8mb4_0900_ai_ci`. An exact existing database is returned as `AlreadyExists` without altering
its character set, collation, grants, or contents. When `lower_case_table_names` is `1` or `2`, a
differently-cased name identifies the same logical database and is also returned as `AlreadyExists`;
when it is `0`, database-name matching remains case-sensitive.
MySQL returns `DatabaseAccessDenied` for both existing and missing database names when the account
cannot see either target, so that observation reports `PermissionDenied` with
`TargetExists == null` instead of guessing that the target exists.

This capability does not provide a migration lock, run EF Core migrations, claim MariaDB
compatibility, create database users, grant database permissions, or equate behavior across managed
MySQL services. Those remain independently registered capabilities and deployment concerns.

### MariaDB target preparation

`ServiceMantle.Database.MariaDb` keeps the canonical `MariaDB` provider identity, registration,
version policy, diagnostics, and implementation path independent from `MySQL`. Hosts opt in
explicitly:

```csharp
services
    .AddServiceMantle(serviceId, instanceId)
    .AddBootstrapDatabaseProvider<MariaDbBootstrapDatabaseProvider>()
    .AddDatabaseTargetPreparationProvider<MariaDbDatabaseTargetPreparationProvider>()
    .AddMigrationLockProvider<MariaDbMigrationLockProvider>();
```

The provider accepts numeric MariaDB 10.11+ server versions and verifies the server's MariaDB
product identity on Bootstrap validation, observation, and preparation. Missing-target and
target-permission observations use a second server-level read probe only to establish product
identity; neither observation path creates or changes an object. Preparation checks product
identity before any creation statement, so a MySQL server configured as `MariaDB` fails closed.

Database identifiers must fit MariaDB's 64-character limit without control characters, NUL,
surrogate code units, or a trailing ASCII space. A missing database is created with `utf8mb4` and
`utf8mb4_uca1400_ai_ci`. An exact existing database is returned as `AlreadyExists` without altering
its character set, collation, grants, or contents. When `lower_case_table_names` is `1` or `2`, a
differently-cased name identifies the same logical database and is also returned as `AlreadyExists`;
when it is `0`, database-name matching remains case-sensitive.
MariaDB returns `DatabaseAccessDenied` for both existing and missing database names when the account
cannot see either target, so that observation reports `PermissionDenied` with
`TargetExists == null` instead of guessing that the target exists.

These capabilities do not run EF Core migrations, claim MySQL compatibility, create database users,
grant database permissions, or equate behavior across managed MariaDB services. They do not attempt
to defeat a proxy that deliberately forges the server product version. Those remain consuming-service
and deployment concerns.

### SQL Server target preparation

`ServiceMantle.Database.SqlServer` keeps the canonical `SqlServer` provider identity independent
from every other database provider. Hosts opt in explicitly:

```csharp
services
    .AddServiceMantle(serviceId, instanceId)
    .AddBootstrapDatabaseProvider<SqlServerBootstrapDatabaseProvider>()
    .AddDatabaseTargetPreparationProvider<SqlServerDatabaseTargetPreparationProvider>()
    .AddMigrationLockProvider<SqlServerMigrationLockProvider>();
```

The provider accepts numeric SQL Server 2019 (major version 15) and later version declarations. It
also verifies the connected server major version before accepting a target or creating a database.
Database names must contain 1 through 123 characters and cannot contain NUL, control characters,
surrogate code units, or a trailing ASCII space. Auto-attach (`AttachDBFilename`) connection strings
are rejected before opening a connection because attaching a file would violate read-only
observation. The 123-character limit accounts for the logical log-file name SQL Server generates
when `CREATE DATABASE` does not specify a file list. Names are bracket-delimited and embedded `]`
characters are doubled.

`ObserveAsync` first attempts a connection to the requested database. SQL Server errors 4060 and
916 do not by themselves prove whether a database is missing or hidden by metadata permissions, so
the provider can use the same target credentials for one read-only query against `master`. It
reports `TargetMissing` only when the account has complete database visibility and no matching
database is visible. Otherwise, a hidden database keeps `TargetExists == null`; a visible online
database without access reports `TargetExists == true`. Observation never invokes creation.

`PrepareAsync` uses the caller-supplied administrative connection only for that call, forces its
catalog to `master`, disables pooling, ambient transaction enlistment, and connection retries, and
bounds connection and command timeouts. It queries `sys.databases` and creates only a proven-missing
database with `Latin1_General_100_CI_AS_SC_UTF8`. An exact existing database is returned as
`AlreadyExists` without changing its collation, files, permissions, metadata, or contents. A
non-exact but collation-equivalent collision under the server collation returns
`database_target_preparation.target_conflict`; a concurrent create is rechecked under the same
rules.

These capabilities do not run EF Core migrations, create logins or users, grant permissions,
configure server or storage settings, or claim equivalent behavior on Azure SQL and other managed
services. The same-server proof above applies before target metadata is accepted or a database
is created; it does not expand the supported routing or managed-service boundary.

### Error codes

Safe error codes for database target preparation failures are restricted to this allowlist; result and observation factories reject arbitrary text:
- `database_target_preparation.capability_not_supported` - No preparation provider registered for the database provider.
- `database_target_preparation.provider_mismatch` - The target does not identify this provider's database provider.
- `database_target_preparation.invalid_target` - The target or administrative connection information is not usable.
- `database_target_preparation.server_unreachable` - The database server could not be reached.
- `database_target_preparation.authentication_failed` - The server rejected the supplied credentials before target existence could be established.
- `database_target_preparation.permission_denied` - The target connection or administrative operation lacked permission.
- `database_target_preparation.target_conflict` - Creation collided with an existing, differently-owned object of the same name.
- `database_target_preparation.connection_failed` - A connection could not be established or was lost while preparing the target.
- `database_target_preparation.timeout` - The preparation operation exceeded its allotted timeout.
- `database_target_preparation.preparation_failed` - Preparation failed for a provider-specific reason not covered by another code.

Caller-requested cancellation is always propagated as a sanitized `OperationCanceledException` without the underlying database exception, distinct from a timeout failure result. PostgreSQL preparation rejects infinite, non-positive, and timer-unsupported timeouts before starting work; an already-cancelled caller token takes precedence over timeout validation.

## Database migration orchestration

ServiceMantle provides provider-agnostic migration orchestration that ensures safe multi-instance execution of consuming service migrations under an optional provider-specific lock.

The orchestration flow:
1. Acquire a provider-specific migration lock (e.g., PostgreSQL advisory lock).
2. Inspect the current database state while monitoring the acquired lease.
3. Skip migration if the database is already at the current compatible version (allowing waiting instances to pass without re-executing).
4. Fail closed if the database version is newer than the application supports.
5. Execute the consuming service's complete migration workflow exactly once while monitoring the lease.
6. Re-inspect the database state under the same monitored lease to ensure migration succeeded.
7. Always release the lock, even on failure or cancellation.

The consuming service implements `IDatabaseMigrationExecutor` to define its migration logic:

```csharp
public interface IDatabaseMigrationExecutor
{
    // Inspect database state: Empty, CurrentVersionCompatible, PendingMigration, VersionTooNew, or InspectionFailed
    ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default);

    // Execute the complete migration workflow
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
}
```

The orchestrator passes a token linked to caller cancellation and the acquired lease's
`IDatabaseMigrationLock.LeaseLost` signal into every inspection and execution. Executors must observe
that token promptly. Caller cancellation retains `OperationCanceledException` semantics even when it
races with lease loss; lease loss instead returns `migration.lock_failed` and prevents any later stage
from starting. This cannot roll back database or external side effects committed before the signal.

The `DatabaseMigrationOrchestrator` is instantiated with the executor and a lock provider registry:

```csharp
var executor = new MyServiceMigrationExecutor(dbContext);
var lockProviders = new DatabaseMigrationLockProviderRegistry(
    [new OracleMigrationLockProvider()],
    providerRegistry.ProviderIdResolver);
var orchestrator = new DatabaseMigrationOrchestrator(executor, lockProviders);

var result = await orchestrator.OrchestrateMigrationAsync(
    serviceId,
    bootstrapDatabaseConfiguration,
    lockAcquireTimeout: TimeSpan.FromSeconds(30));

if (!result.Succeeded)
{
    // Safe error code and message without exposing secrets
    logger.LogError("Migration failed: {ErrorCode}: {Message}", result.ErrorCode, result.ErrorMessage);
}
```

### PostgreSQL advisory lock

`ServiceMantle.Database.PostgreSql.Migration.PostgreSqlMigrationLockProvider` provides session-level advisory locks using `pg_try_advisory_lock` with bounded polling. The lock key is derived from the service identifier using SHA-256, ensuring stability across processes, machines, and restarts.

Lock acquisition respects both the caller-provided timeout and cancellation token. The lock is held
for the lifetime of the returned lease object, and is released either explicitly (`DisposeAsync`) or
implicitly when the connection closes. The acquired lease probes its dedicated connection every 250
milliseconds with a one-second command timeout and exposes detected session loss through
`LeaseLost`. The running-process detection bound is five seconds, including scheduling margin.
Process suspension, severe thread-pool starvation, or an environment that prevents command timeout
delivery is outside that bound; detection is not zero-latency.

### Oracle DBMS_LOCK

`ServiceMantle.Database.Oracle.Migration.OracleMigrationLockProvider` derives a stable name as
`ServiceMantle.Migration.` followed by the lowercase SHA-256 digest of the normalized `ServiceId`.
It obtains the same-name handle with `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS`, requests `X_MODE` with
the remaining bounded acquisition timeout and `release_on_commit => FALSE`, then explicitly releases
the handle before closing the dedicated session. Missing direct package access fails with
`migration.lock_not_supported`. Unsupported declared versions or password identities, proven
unsupported runtime topologies, and denied topology probes use the same code; these are rejected
before lock allocation or request. Malformed configuration (provider/version/connection syntax or
missing data source), session identity mismatches, authentication/transport failures, and unknown
SQL failures remain `migration.lock_failed`. Null arguments and invalid acquisition timeouts retain
argument exceptions. Caller cancellation takes precedence over classified operational failures.
Errors contain no connection, identity, or underlying exception details.

The lease probes its session every 250 milliseconds with a one-second command timeout and signals
detected loss within the same conservative five-second running-process bound as the PostgreSQL
provider. That signal prevents orchestration from reporting success after Oracle has released a lock
because its session ended. It cannot undo consumer side effects performed before loss was detected,
and it is not a fencing token.

Oracle RAC and migration-lease transfer across session failover or transparent replay are
unsupported. A recovered connection does not establish ownership of the original lock. Use a
single-instance PDB with a dedicated session that cannot be transparently replaced; a failed
migration must restart as a new orchestration call. See the
[RAC and failover decision](docs/decisions/0005-oracle-rac-and-failover.md) for the support boundary
and the evidence required before it can be expanded.

### MariaDB named lock

`ServiceMantle.Database.MariaDb.Migration.MariaDbMigrationLockProvider` uses MariaDB `GET_LOCK` on
an unpooled, non-enlisted target-database session. Its exact 64-byte ASCII name is `sm:migration:`
followed by the first 51 lowercase hexadecimal characters of the normalized `ServiceId` SHA-256
digest. Connection, MariaDB product and database-identity validation, and the parameterized
`GET_LOCK` call share one positive bounded acquisition timeout; timeout returns
`migration.lock_timeout`, while SQL, product, identity, and null-result failures return the safe
`migration.lock_failed` classification.

The lease probes the same connection identity every 250 milliseconds with a one-second command
timeout and signals permanent `LeaseLost` within the conservative five-second running-process bound.
Disposal attempts `RELEASE_LOCK` once and then closes the dedicated session as the authoritative
fallback. The lock is session-scoped rather than transaction-scoped, recursive acquisition is not a
supported usage, and the lease is not a fencing token; it cannot undo side effects committed before
loss is detected.


### SQL Server application lock

`ServiceMantle.Database.SqlServer.Migration.SqlServerMigrationLockProvider` derives the resource as
`ServiceMantle.Migration.` followed by the full 64-character lowercase SHA-256 digest of the
normalized `ServiceId`. It opens the configured target database with pooling, ambient enlistment,
and connection retries disabled, verifies SQL Server 2019+ and `DB_NAME()`, and calls
`sys.sp_getapplock` with `Exclusive`, `Session`, and the `public` database principal. Connection,
identity validation, and lock acquisition share one positive timeout no greater than
`int.MaxValue` milliseconds.

Return codes 0 and 1 acquire the lease; -1 is `migration.lock_timeout`, while cancellation,
deadlock, parameter, SQL, identity, and other failures retain the documented safe cancellation or
`migration.lock_failed` behavior. A direct application-lock permission denial is
`migration.lock_not_supported`. The lease verifies `APPLOCK_MODE` and its session identity every
250 milliseconds with a one-second command timeout, signalling permanent `LeaseLost` within the
conservative five-second running-process bound. Disposal attempts `sp_releaseapplock` once and then
closes the dedicated session as the authoritative fallback. The lock is scoped to one database,
uses session rather than transaction ownership, is not a fencing token, and cannot undo already
committed migration side effects.

No lock providers for SQLite or MySQL are implemented in this release.
Multi-instance migrations without registered lock support fail closed with
`migration.lock_not_supported`.

### Error codes

Safe error codes for migration failures:
- `migration.lock_not_supported` - No lock provider registered for the database.
- `migration.lock_timeout` - Lock acquisition exceeded the timeout.
- `migration.lock_failed` - Lock acquisition failed or an acquired lease was lost before completion.
- `migration.inspection_failed` - Database state could not be determined.
- `migration.version_too_new` - Database schema is newer than the application.
- `migration.execution_failed` - The consuming service's migration executor failed.
- `migration.final_state_invalid` - Database state after migration is not compatible.

## Non-goals (first version)

- No product-specific user / OAuth / JWT domain models.
- No management frontend.
- No business migration or service-specific logic.

## Structured logging security

The core package provides a sink-neutral, fail-closed structured value sanitizer. Its guaranteed
field/Header/type boundaries and deliberately limited free-text detection contract are documented in
[`LOGGING_SECURITY.md`](LOGGING_SECURITY.md).

`ServiceMantle.AspNetCore` can opt into one startup-time sensitive request Header snapshot and the
ServiceMantle-owned diagnostic projection that consumes it:

```csharp
var serviceMantle = builder.AddServiceMantle(
    ServiceId.Parse("catalog"),
    InstanceId.Parse("catalog-01"));

serviceMantle.AddSensitiveHeaders(options =>
{
    options.DeniedHeaderNames = ["X-Deployment-Secret"];
});

var projector = app.Services.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>();
IReadOnlyDictionary<string, object?> safeHeaders = projector.Project(httpContext.Request.Headers);
```

The immutable snapshot always contains the built-in authentication, cookie, and API-key names from
`StructuredLogSanitizerDefaults.BuiltInDeniedHeaderNames`. Consumer additions use HTTP token syntax,
merge case-insensitively, and cannot remove a built-in. Invalid names, collection enumeration
failures, and a separately registered `StructuredLogSanitizer` fail when the Host starts using only
stable error metadata. The DI-provided sanitizer and request projector consume the same snapshot;
denied single-value and multi-value Headers both become `[REDACTED]`.
`AddSensitiveHeaders` and `AddServiceMantleSerilog` can be called in either order and share that
snapshot. A sanitizer registered separately by the consumer remains a startup conflict.

The registry has no runtime update or removal API. Product-specific names must be added explicitly.
It does not mutate the original request Headers and does not govern third-party logging providers,
message templates, Activity tags, tracing exporters, or any path that bypasses the projector.

The optional `ServiceMantle.Serilog` package installs a Serilog Console pipeline whose structured
properties always pass through that sanitizer:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceMantleSerilog(options =>
{
    options.MinimumLevel = "Information";
    options.EnricherNames = ["FromLogContext"];
    options.FlushTimeout = TimeSpan.FromSeconds(2);
});
```

The default output template is deterministic and includes structured properties. Equivalent
normalized registrations are idempotent. Invalid option values, conflicting registrations, or an
existing Serilog configuration fail when the Host starts, using only stable field names and error
codes. Public options cannot disable or bypass structured-property sanitization. Sanitizer failures
emit `[SANITIZATION_FAILED]`, and controlled shutdown or an observable unhandled exception starts a
bounded, exactly-once flush.

Registration removes existing `Microsoft.Extensions.Logging` providers so that another Console
provider cannot receive unsanitized properties. Add any intentional non-Console providers after this
extension. This package does not sanitize caller-interpolated free text in message templates and
cannot flush after forced termination such as `SIGKILL`, a host crash, or stack overflow.

The separate `ServiceMantle.Serilog.GrafanaLoki` package adds an opt-in remote sink after the same
mandatory sanitizer. Authentication is resolved at startup from a non-secret name and is never part
of the options snapshot:

```csharp
builder.AddServiceMantleSerilog();
builder.Services.AddSingleton<IServiceMantleLokiAuthorizationHeaderResolver, LokiAuthorizationResolver>();
builder.AddServiceMantleGrafanaLoki(options =>
{
    options.Enabled = true;
    options.Endpoint = new Uri("https://logs.example.com/grafana");
    options.AuthorizationHeaderResolverName = "primary-loki";
    options.BatchSize = 100;
    options.QueueLimit = 1_000;
    options.FlushPeriod = TimeSpan.FromSeconds(2);
    options.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
});
```

Enabled endpoints must use absolute HTTPS URIs without user information, query strings, or
fragments. An explicit test-only option permits loopback HTTP. Batch size is limited to 1-1,000,
queue capacity to 100-50,000 events, and flush and shutdown drain periods to 1-30 seconds. Invalid
configuration, a missing resolver, or an unavailable authorization value fails when the Host starts
without including submitted values in the exception.

The fixed upstream driver owns the bounded in-memory queue and retry schedule. Capacity drops,
permanent delivery failures, drain timeouts, and caller-cancelled drains are exposed only through
content-free counters and stable error codes on `ServiceMantleGrafanaLokiDiagnostics`. The package
does not add disk buffering, unbounded retries, dynamic reload, query APIs, or exactly-once delivery.

## Frontend note

Frontend work is intentionally out of scope and will be implemented in a separate `ServiceMantle.Console` project.

## Repository layout

- `src/ServiceMantle/ServiceMantle.csproj`
- `src/ServiceMantle.AspNetCore/ServiceMantle.AspNetCore.csproj`
- `src/ServiceMantle.Database.Sqlite/ServiceMantle.Database.Sqlite.csproj`
- `src/ServiceMantle.Database.SqlServer/ServiceMantle.Database.SqlServer.csproj`
- `src/ServiceMantle.Serilog/ServiceMantle.Serilog.csproj`
- `src/ServiceMantle.Serilog.GrafanaLoki/ServiceMantle.Serilog.GrafanaLoki.csproj`
- `tests/ServiceMantle.AspNetCore.Tests/ServiceMantle.AspNetCore.Tests.csproj`
- `tests/ServiceMantle.Database.Sqlite.Tests/ServiceMantle.Database.Sqlite.Tests.csproj`
- `tests/ServiceMantle.Database.SqlServer.Tests/ServiceMantle.Database.SqlServer.Tests.csproj`
- `tests/ServiceMantle.Serilog.Tests/ServiceMantle.Serilog.Tests.csproj`
- `tests/ServiceMantle.Serilog.GrafanaLoki.Tests/ServiceMantle.Serilog.GrafanaLoki.Tests.csproj`
- `tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj`
- `src/ServiceMantle/ServiceId.cs`
- `src/ServiceMantle/InstanceId.cs`
- `src/ServiceMantle/Installation/`
- `src/ServiceMantle.Persistence.EntityFrameworkCore/ServiceMantle.Persistence.EntityFrameworkCore.csproj`
- `tests/ServiceMantle.Tests/ServiceIdTests.cs`
- `tests/ServiceMantle.Tests/InstanceIdTests.cs`
- `tests/ServiceMantle.Tests/Installation/`
- `tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests.csproj`
- `ServiceMantle.slnx`
- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props`

## Local build commands

Standard build and test:

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release
dotnet test --solution ServiceMantle.slnx -c Release
dotnet pack src/ServiceMantle/ServiceMantle.csproj -c Release --no-build
dotnet pack src/ServiceMantle.AspNetCore/ServiceMantle.AspNetCore.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Serilog/ServiceMantle.Serilog.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Serilog.GrafanaLoki/ServiceMantle.Serilog.GrafanaLoki.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Database.PostgreSql/ServiceMantle.Database.PostgreSql.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Database.Sqlite/ServiceMantle.Database.Sqlite.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Database.SqlServer/ServiceMantle.Database.SqlServer.csproj -c Release --no-build
dotnet pack src/ServiceMantle.Persistence.EntityFrameworkCore/ServiceMantle.Persistence.EntityFrameworkCore.csproj -c Release --no-build
```

With PostgreSQL Testcontainers (requires Docker):

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx -c Release
```

To run only PostgreSQL container tests:

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --project tests/ServiceMantle.Database.PostgreSql.Tests -c Release
```

To override PostgreSQL image:

```bash
SERVICEMANTLE_POSTGRES_IMAGE=postgres:16 RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx -c Release
```

With SQL Server Testcontainers (requires Docker on a supported Linux/AMD64 host):

```bash
RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Database.SqlServer.Tests -c Release
RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests -c Release
```

To override the SQL Server image:

```bash
SERVICEMANTLE_SQLSERVER_IMAGE=mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04 RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Database.SqlServer.Tests -c Release
```

// Proves that a consumer can import three ServiceMantle modules at once, next to the ASP.NET Core
// implicit usings, and still name every configuration type without a using alias or a fully
// qualified name.
//
// That is the property the naming rules have to hold: dropping the product prefix from ordinary
// types must not hand the caller an ambiguity between, say, ServiceMantle's forwarded-header trust
// boundary and Microsoft.AspNetCore.Builder.ForwardedHeadersOptions. Every renamed public type this
// file touches is named unqualified on purpose - the file failing to compile is the assertion.
using Microsoft.AspNetCore.Authorization;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.Logging;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.AspNetCore.RateLimiting;
using ServiceMantle.OpenTelemetry;
using ServiceMantle.OpenTelemetry.Otlp;
using ServiceMantle.OpenTelemetry.Prometheus;
using ServiceMantle.Serilog;
using ServiceMantle.Serilog.GrafanaLoki;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddAuthorization(options => options.AddPolicy(
        "metrics-scrape",
        policy => policy.RequireAssertion(_ => true)));

    builder.AddServiceMantleSerilog(options => options.MinimumLevel = "Information");
    builder.AddServiceMantleGrafanaLoki(options => options.Enabled = false);

    ServiceMantleBuilder serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    serviceMantle.AddServiceMantleHealthEndpoints(options => options.ProbeTimeout = TimeSpan.FromSeconds(5));
    serviceMantle.AddServiceMantlePhaseGate(options => options.ManagementPathPrefix = "/management");
    serviceMantle.AddForwardedHeaders(options =>
    {
        options.KnownProxies = ["127.0.0.1"];
        options.ForwardLimit = 1;
    });
    serviceMantle.AddSecurityResponseHeaders();
    serviceMantle.AddRateLimiting(options => options.Management.PermitLimit = 100);
    serviceMantle.AddSensitiveHeaders(options => options.DeniedHeaderNames = ["x-consumer-secret"]);
    serviceMantle.AddOpenTelemetryInstrumentation();
    serviceMantle.AddOpenTelemetryOtlpExporter(options =>
    {
        options.Traces.Enabled = true;
        options.Traces.Protocol = OtlpProtocol.Grpc;
        options.Traces.Endpoint = new Uri("https://collector.invalid:4317/");
    });
    serviceMantle.AddOpenTelemetryPrometheusEndpoint(options =>
    {
        options.Enabled = true;
        options.EndpointPath = "/metrics";
        options.AuthorizationPolicyName = "metrics-scrape";
    });

    var application = builder.Build();
    application.UseServiceMantleForwardedHeaders();
    application.UseServiceMantleCorrelationId();
    application.UseServiceMantleProblemDetails();
    application.UseServiceMantleSecurityResponseHeaders();
    application.UseServiceMantlePhaseGate();
    application.UseAuthorization();
    application.MapServiceMantleHealthEndpoints();
    application.MapServiceMantlePrometheusEndpoint();
    application
        .MapGet("/management/status/ping", () => "ok")
        .WithServiceMantleManagementSurface(ManagementSurface.Status);

    await application.StartAsync();

    // Naming the types is the point; the values are incidental.
    Report<HealthOptions>();
    Report<PhaseGateOptions>();
    Report<ForwardedHeadersTrustOptions>();
    Report<RateLimitingOptions>();
    Report<RateLimitPolicyOptions>();
    Report<SensitiveHeadersOptions>();
    Report<SensitiveHeaderRegistry>();
    Report<SecurityResponseHeadersMetadata>();
    Report<OpenTelemetryOptions>();
    Report<ServiceMetrics>();
    Report<OtlpOptions>();
    Report<OtlpTraceOptions>();
    Report<PrometheusOptions>();
    Report<SerilogOptions>();
    Report<GrafanaLokiOptions>();
    Console.WriteLine($"Correlation ID header: {ServiceHeaderNames.CorrelationId}.");
    Console.WriteLine($"Problem type prefix: {ProblemDetailsDefaults.TypeUriPrefix}.");
    Console.WriteLine("Composed consumer started; AspNetCore, OpenTelemetry, and Serilog all resolved.");

    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}

static void Report<T>() => Console.WriteLine($"Resolved {typeof(T).FullName}.");

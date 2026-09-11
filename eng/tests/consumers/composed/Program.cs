// Proves that a consumer can import four ServiceMantle modules at once, next to the ASP.NET Core
// implicit usings and the framework namespaces each module's own entry points require, and still
// name every public type without a using alias or a fully qualified name.
//
// That is the property the naming rules have to hold: dropping the product prefix from ordinary
// types must not hand the caller an ambiguity between, say, ServiceMantle's forwarded-header trust
// boundary and Microsoft.AspNetCore.Builder.ForwardedHeadersOptions. Every renamed public type this
// file touches is named unqualified on purpose - the file failing to compile is the assertion.
//
// Microsoft.AspNetCore.DataProtection and Microsoft.EntityFrameworkCore are imported for the same
// reason: reaching the EF Core persistence entry points requires them, so their types are in scope
// whenever that module is, and any ServiceMantle type sharing a name with one of them would be
// ambiguous here.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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
using ServiceMantle.Persistence.EntityFrameworkCore;
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

    // The EF Core persistence module's public surface is mostly static extension containers, whose
    // names are the ones most likely to already exist in a framework namespace - Data Protection
    // alone ships DataProtectionBuilderExtensions. Naming every one of them here, unqualified and
    // with the framework namespaces above in scope, is what turns such a collision into a build
    // failure rather than a CS0104 the consumer discovers.
    ReportType(typeof(EfCoreDataProtectionExtensions));
    ReportType(typeof(ModelBuilderExtensions));
    ReportType(typeof(DataProtectionKeyModelBuilderExtensions));
    ReportType(typeof(ManagementAuditModelBuilderExtensions));
    ReportType(typeof(ServiceSettingModelBuilderExtensions));
    ReportType(typeof(IServiceDbContext));
    ReportType(typeof(ManagementAuditDatabaseDialect));
    ReportType(typeof(WellKnownDataProtectionKeyRepositoryErrorCodes));
    ReportType(typeof(DataProtectionKeyRepositoryException));
    ReportType(typeof(ServiceInstallationEntity));
    ReportType(typeof(EfCoreDataProtectionKeyRepository<>));
    ReportType(typeof(EfCoreServiceInstallationStore<>));
    ReportType(typeof(EfCoreServiceSettingStore<>));
    ReportType(typeof(EfCoreServiceSettingUpdateTransaction<>));
    ReportType(typeof(EfCoreServiceSetupCodeStore<>));
    ReportType(typeof(EfCoreManagementAuditWriter<>));
    ReportType(typeof(EfCoreManagementAuditQueryService<>));

    Console.WriteLine($"Correlation ID header: {ServiceHeaderNames.CorrelationId}.");
    Console.WriteLine($"Problem type prefix: {ProblemDetailsDefaults.TypeUriPrefix}.");
    Console.WriteLine(
        "Composed consumer started; AspNetCore, OpenTelemetry, Serilog, and "
        + "Persistence.EntityFrameworkCore all resolved.");

    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}

static void Report<T>() => Console.WriteLine($"Resolved {typeof(T).FullName}.");

// Static classes and open generics cannot be type arguments, so they are named through typeof.
static void ReportType(Type type) => Console.WriteLine($"Resolved {type.FullName}.");

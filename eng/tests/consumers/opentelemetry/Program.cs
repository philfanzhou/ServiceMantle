// Proves that one package reference - ServiceMantle.OpenTelemetry - is enough to reach every
// entry point that used to require ServiceMantle.OpenTelemetry.Otlp and
// ServiceMantle.OpenTelemetry.Prometheus as separate packages.
//
// It also proves the provider-neutral contract boundary: resolving ServiceMetrics and publishing
// an installation phase requires no provider-named using at all. ServiceMetrics lives in the core
// package's ServiceMantle.Diagnostics namespace, and the registration entry lives in
// Microsoft.Extensions.DependencyInjection, so this file deliberately contains no
// using ServiceMantle.OpenTelemetry. Only the OTLP configuration types, which the boundary review
// classified as provider-specific, are reached through ServiceMantle.OpenTelemetry.Otlp.
using Microsoft.AspNetCore.Authorization;
using ServiceMantle;
using ServiceMantle.Diagnostics;
using ServiceMantle.Installation;
using ServiceMantle.OpenTelemetry.Otlp;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddAuthorization(options => options.AddPolicy(
        "metrics-scrape",
        policy => policy.RequireAssertion(_ => true)));

    var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    serviceMantle.AddServiceMantleMetrics();
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
    application.UseAuthorization();
    application.MapServiceMantlePrometheusEndpoint();

    await application.StartAsync();
    // The type is named unqualified through ServiceMantle.Diagnostics only; that this compiles
    // and resolves is the assertion, the phase value is incidental.
    var metrics = application.Services.GetRequiredService<ServiceMetrics>();
    metrics.SetPhase(ServiceStartupPhase.Completed);
    Console.WriteLine(
        "ServiceMantle.OpenTelemetry consumer started; instrumentation, fixed metrics, OTLP, and " +
        $"Prometheus all resolved from {typeof(OtlpOptions).Assembly.Location}, while " +
        $"{typeof(ServiceMetrics).FullName} resolved without any provider-named using.");
    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}

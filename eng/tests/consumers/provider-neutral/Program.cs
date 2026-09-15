// The ADR 0007 category-A boundary as a compile-time assertion: this single compilation unit
// references all three optional provider packages at once and names every category-A-migrated
// contract through the neutral namespaces only - the telemetry contracts and the phase publisher
// through ServiceMantle.Diagnostics, the log authorization resolver and delivery diagnostics
// through ServiceMantle.Logging, and the registration lifecycle timing through
// ServiceMantle.Discovery. Next to those, the framework namespaces the entries live in are all
// in scope: the Web SDK implicit usings plus explicit Microsoft.Extensions.DependencyInjection,
// Microsoft.Extensions.Hosting, Microsoft.Extensions.Logging, and
// Microsoft.AspNetCore.Authorization. Any category-A type that moved back into a provider-named
// namespace, or that collides with a framework type of the same name, fails this file at compile
// time instead of a consumer's.
//
// Provider names appear in exactly two places: the package references in Consumer.csproj and the
// Add*() method names below. The remote endpoints are deliberately invalid hosts; nothing here
// asserts delivery, only that the neutral contracts compile and resolve side by side.
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Diagnostics;
using ServiceMantle.Discovery;
using ServiceMantle.Installation;
using ServiceMantle.Logging;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-provider-neutral");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Services.AddAuthorization(options => options.AddPolicy(
        "provider-neutral",
        policy => policy.RequireAssertion(_ => true)));

    // The neutral resolver contracts, implemented and registered without any provider-named
    // using. The returned values are placeholders, not secrets.
    builder.Services.AddSingleton<IRemoteLogAuthorizationResolver, NeutralLogResolver>();
    builder.Services.AddSingleton<IRemoteTelemetryAuthenticationResolver>(
        new FixedTelemetryResolver("trace-auth", "provider-neutral-placeholder"));

    var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("provider-neutral-consumer"),
        InstanceId.Parse("provider-neutral-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    serviceMantle.AddServiceMantleMetrics();
    serviceMantle.AddOpenTelemetryOtlpExporter(options =>
    {
        options.Traces.Enabled = true;
        options.Traces.Endpoint = new Uri("https://collector.invalid:4317/");
        options.Traces.AuthenticationHeaderName = "trace-auth";
    });

    builder.AddServiceMantleSerilog(options => options.MinimumLevel = LogLevel.Information);
    builder.AddServiceMantleGrafanaLoki(options =>
    {
        options.Enabled = true;
        options.Endpoint = new Uri("https://loki.invalid/loki/api/v1/push");
        options.AuthorizationHeaderResolverName = "provider-neutral-consumer";
    });

    var application = builder.Build();
    await application.StartAsync();

    // The phase publisher is named unqualified through ServiceMantle.Diagnostics; that this
    // resolves is the assertion, the phase value is incidental.
    var metrics = application.Services.GetRequiredService<ServiceMetrics>();
    metrics.SetPhase(ServiceStartupPhase.Completed);

    // The delivery diagnostics counter is named unqualified through ServiceMantle.Logging and
    // read without asserting any values.
    var deliveryDiagnostics = application.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
    Console.WriteLine(
        "Provider-neutral consumer started with all three optional provider packages: the " +
        $"phase publisher resolved as {typeof(ServiceMetrics).FullName}, and the delivery " +
        $"diagnostics report {deliveryDiagnostics}.");

    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}

// The registration lifecycle entry is configured on a separate collection without starting any
// Consul lifecycle: the timing type is the neutral one from ServiceMantle.Discovery, and the
// lambda names it explicitly so the parameter type is part of the assertion.
var registrationServices = new ServiceCollection();
registrationServices.AddServiceMantleConsul((ServiceRegistrationLifecycleOptions options) =>
{
    options.ReadinessPollInterval = TimeSpan.FromSeconds(1);
    options.ReadinessCallBudget = TimeSpan.FromSeconds(10);
    options.OperationBudget = TimeSpan.FromSeconds(10);
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    options.MaximumRetryDelay = TimeSpan.FromSeconds(5);
    options.ShutdownBudget = TimeSpan.FromSeconds(15);
});

if (registrationServices.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)) != 1)
{
    throw new InvalidOperationException(
        "The Consul registration entry did not produce exactly one hosted lifecycle.");
}

Console.WriteLine(
    $"Registration timing configured through {typeof(ServiceRegistrationLifecycleOptions).FullName}: " +
    $"{registrationServices.Count} descriptors and one hosted lifecycle.");

// The neutral log authorization contract from ServiceMantle.Logging: nothing in its declaration
// or registration names a provider or a backend product.
sealed class NeutralLogResolver : IRemoteLogAuthorizationResolver
{
    public string? ResolveAuthorizationHeader(string name) => "Bearer provider-neutral-placeholder";
}

// The neutral telemetry authentication contract from ServiceMantle.Diagnostics: the provider
// package resolves it at host startup and never sees the header value in diagnostics.
sealed class FixedTelemetryResolver(string resolvedName, string resolvedValue)
    : IRemoteTelemetryAuthenticationResolver
{
    public bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header)
    {
        if (name != resolvedName)
        {
            header = null;
            return false;
        }

        header = new RemoteTelemetryAuthenticationHeader(resolvedName, resolvedValue);
        return true;
    }
}

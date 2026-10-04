// The ADR 0007 category-A boundary as a compile-time assertion: this single compilation unit
// references all three optional provider packages at once and names every category-A-migrated
// contract through the neutral namespaces only - the telemetry contracts and the phase publisher
// through ServiceMantle.Diagnostics, the log authorization resolver and delivery diagnostics
// through ServiceMantle.Logging, and the registration lifecycle timing through
// ServiceMantle.Discovery. The two contracts uplifted from the ASP.NET Core adapter (#572), the
// startup phase resolver and the health snapshot source, are named through
// ServiceMantle.Installation and ServiceMantle.Health. Next to those, the framework namespaces
// the entries live in are all in scope: the Web SDK implicit usings plus explicit
// Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Hosting,
// Microsoft.Extensions.Logging, and Microsoft.AspNetCore.Authorization. Any category-A type that
// moved back into a provider-named namespace, or that collides with a framework type of the same
// name, fails this file at compile time instead of a consumer's.
//
// Provider names appear in exactly two places: the package references in Consumer.csproj and the
// Add*() method names below. The remote endpoints are deliberately invalid hosts; nothing here
// asserts delivery, only that the neutral contracts compile and resolve side by side.
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Configuration;
using ServiceMantle.Diagnostics;
using ServiceMantle.Discovery;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Logging;
using ServiceMantle.Migration;

var externalRootSource = new RootKeyFileSource(Path.Combine(Path.GetTempPath(), "servicemantle-neutral-private", "root.key"));
Func<string> externalRootResolver = externalRootSource.Resolve;
_ = externalRootResolver; // Explicit synchronous source without adapter-owned types or I/O at construction.
var composedRootSource = new RootKeySource("neutral-consumer-root-placeholder", "unrelated\0.key");
if (composedRootSource.Resolve() != "neutral-consumer-root-placeholder" ||
    composedRootSource.ToString() != "RootKeySource(Lazy=True)")
    throw new InvalidOperationException("The injected root source did not preserve the captured value.");

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
        new FixedRemoteTelemetryAuthenticationResolver("trace-auth", "Authorization", "consumer-placeholder"));

    // The two contracts uplifted from the ASP.NET Core adapter into the core package (#572): the
    // consumer-owned health snapshot source is implemented and registered through
    // ServiceMantle.Health, next to the framework usings above, without the hosting adapter's
    // own namespaces (capability-prefixed since the #573 rename) in scope.
    builder.Services.AddSingleton<IServiceHealthSnapshotSource, NeutralSnapshotSource>();

    var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("provider-neutral-consumer"),
        InstanceId.CreateRandom(ServiceId.Parse("provider-neutral-consumer")),
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

    // The uplifted startup phase resolver (#572) is named unqualified through
    // ServiceMantle.Installation and resolved from the AddServiceMantle registration; the
    // resolved interface's assembly is the assertion, the phase value is incidental.
    var phaseResolver = application.Services.GetRequiredService<IServiceStartupPhaseResolver>();
    _ = phaseResolver.Resolve(hasBootstrapConfiguration: false, installationState: null);

    Console.WriteLine(
        "Provider-neutral consumer started with all three optional provider packages: the " +
        $"phase publisher resolved as {typeof(ServiceMetrics).FullName}, the delivery " +
        $"diagnostics report {deliveryDiagnostics}, and the phase resolver interface is " +
        $"{typeof(IServiceStartupPhaseResolver).FullName}.");

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

// The core schema evidence contracts from ServiceMantle.Migration (#608): the neutral models,
// the pure comparer, and the target read result are named unqualified next to every framework
// namespace in scope above. Comparing a one-column expected table against a drifted snapshot is
// the whole assertion — pure data in, differences out, no I/O anywhere.
var expectedSchema = new ExpectedSchema(
[
    new SchemaTable(
        "orders",
        [new SchemaColumn("id", "integer", isNullable: false, SchemaIdentityKind.Always)],
        new SchemaPrimaryKey(["id"])),
]);
var driftedSnapshot = new SchemaSnapshot(
[
    new SchemaTable(
        "orders",
        [new SchemaColumn("id", "int4", isNullable: false, SchemaIdentityKind.Always)],
        new SchemaPrimaryKey(["id"])),
]);

var schemaDifferences = SchemaEvidenceComparer.Compare(driftedSnapshot, expectedSchema);
if (schemaDifferences.Count != 1 ||
    schemaDifferences[0].Kind != SchemaDifferenceKind.ColumnTypeMismatch)
{
    throw new InvalidOperationException(
        "The schema evidence comparer did not report the drifted column type.");
}

var targetMissing = SchemaEvidenceReadResult.TargetDatabaseMissing("catalog");
if (targetMissing.State != SchemaEvidenceReadState.TargetDatabaseMissing ||
    targetMissing.Snapshot is not null ||
    targetMissing.Message.Length == 0)
{
    throw new InvalidOperationException(
        "The schema evidence read result did not distinguish the missing-target fact.");
}

Console.WriteLine(
    $"Schema evidence compared through {typeof(SchemaEvidenceComparer).FullName}: " +
    $"{schemaDifferences.Count} difference, and the read result state is {targetMissing.State}.");

var catalogIndex = new SchemaIndex([], false, "ix_expression", 1, ["id"]);
var catalogForeignKey = new SchemaForeignKey(["id"], "parent", ["id"],
    SchemaForeignKeyDeleteRule.Cascade, null, "fk_parent");
var catalogTable = new SchemaTable("catalog", [new SchemaColumn("id", "integer", false)],
    new SchemaPrimaryKey(["id"], "pk_catalog"), [catalogForeignKey], [catalogIndex]);
var catalogDifferences = SchemaEvidenceComparer.Compare(new SchemaSnapshot([catalogTable]),
    new ExpectedSchema([catalogTable]), new SchemaEvidenceComparisonOptions(true, true));
if (catalogDifferences.Count != 0 || !catalogIndex.HasExpressionKeys)
    throw new InvalidOperationException("Catalog evidence did not round-trip.");

// The neutral log authorization contract from ServiceMantle.Logging: nothing in its declaration
// or registration names a provider or a backend product.
sealed class NeutralLogResolver : IRemoteLogAuthorizationResolver
{
    public string? ResolveAuthorizationHeader(string name) => "Bearer provider-neutral-placeholder";
}

// The uplifted health snapshot contract from ServiceMantle.Health (#572): the consumer owns the
// snapshot; the values here are placeholders, and the source is never resolved by this program.
sealed class NeutralSnapshotSource : IServiceHealthSnapshotSource
{
    public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable));
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

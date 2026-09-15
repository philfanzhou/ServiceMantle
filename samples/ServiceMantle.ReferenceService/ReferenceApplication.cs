using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Database.Sqlite;
using ServiceMantle.ReferenceService.Health;
using ServiceMantle.ReferenceService.Health.PostgreSql;
using ServiceMantle.ReferenceService.Installation;
using ServiceMantle.ReferenceService.Logging;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.ReferenceService.Telemetry;

namespace ServiceMantle.ReferenceService;

/// <summary>Composes the consumer-owned reference host without activating downstream capabilities.</summary>
public static class ReferenceApplication
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("reference-service"),
            InstanceId.Parse("reference-local"));
        // The switch is explicit and fixed before Build: a missing or unparsable value leaves the
        // ServiceMantle Serilog host, the sensitive Header registry, and the request log unwired.
        if (bool.TryParse(builder.Configuration[ReferenceLoggingDefaults.EnabledKey], out var logging) && logging)
        {
            builder.Services.AddSingleton<ReferenceLoggingRegistration>();
            builder.AddServiceMantleSerilog();
            mantle.AddSensitiveHeaders(options =>
                options.DeniedHeaderNames = [ReferenceLoggingDefaults.SecretHeaderName]);
        }
        // Explicit and fixed before Build, on the same shape as the logging switch: only a value
        // that parses to true registers the base ASP.NET Core, HttpClient, and runtime
        // instrumentation. No exporter, no Prometheus endpoint, and no fixed phase metric is wired
        // here - those stay with the tasks that own them.
        mantle.AddReferenceTelemetry(builder.Configuration);
        // Explicit and fixed before Build. When a switch is off nothing below changes, and an
        // unusable input fails here - before a provider, a file, a network, or EF is touched. Both
        // gates are read first so that enabling both is refused before any gate registration or
        // database side effect, with a message naming only the two settings.
        var sqliteOptions = ReferenceSqliteStartupOptions.Read(builder.Configuration);
        var postgresqlOptions = ReferencePostgreSqlStartupOptions.Read(builder.Configuration);
        if (sqliteOptions is not null && postgresqlOptions is not null)
        {
            throw new InvalidOperationException(
                "The reference service refuses to run two startup deployment gates at once: '" +
                ReferenceSqliteStartupOptions.EnabledKey + "' and '" +
                ReferencePostgreSqlStartupOptions.EnabledKey + "' cannot both be true.");
        }

        var sqliteStartup = sqliteOptions is null
            ? null
            : builder.Services.AddReferenceSqliteStartup(sqliteOptions);
        if (postgresqlOptions is not null)
        {
            builder.Services.AddReferencePostgreSqlStartup(postgresqlOptions);
            // The health capability, its one live snapshot source, and the business readiness
            // contributor are wired if and only if the PostgreSQL startup gate is enabled, and they
            // reuse the gate's own context factory and Ready result rather than any new setting. The
            // source re-reads the installation row per request, so the reported phase is
            // authoritative database fact, not the gate's frozen startup phase. The contributor
            // registered here is the business one; the placeholder contributor below is deliberately
            // not registered, because both share Order 100 and the health validator refuses a
            // duplicate order at startup.
            builder.Services.AddSingleton<
                IServiceHealthSnapshotSource,
                ReferencePostgreSqlHealthSnapshotSource>();
            mantle.AddServiceMantleHealthEndpoints();
            mantle.AddServiceReadinessContributor<ReferencePostgreSqlWorkspaceReadinessContributor>();
        }

        var databasePath = builder.Configuration["ReferenceService:DatabasePath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "reference.db");
        var connectionString = sqliteStartup?.TargetConnectionString
            ?? new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ConnectionString;
        builder.Services.AddDbContext<ReferenceDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddSingleton<IServiceSettingDefinitionProvider, ReferenceSettingDefinitions>();
        builder.Services.AddSingleton(provider => new ServiceSettingDefinitionRegistry(
            provider.GetServices<IServiceSettingDefinitionProvider>()));
        builder.Services.AddScoped<IServiceSetupContributor, ReferenceSetupContributor>();
        if (postgresqlOptions is null)
        {
            // With the PostgreSQL gate off, the placeholder stays the single readiness contributor and
            // never claims ready; no health endpoint or snapshot source is registered on this path.
            builder.Services.AddSingleton<IServiceReadinessContributor, ReferenceReadinessContributor>();
        }

        builder.Services.AddScoped<IManagementIdentityProvider, ExternalManagementIdentityPlaceholder>();
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        if (app.Services.GetService<ReferenceLoggingRegistration>() is not null)
        {
            // Correlation stays outside Problem Details, so the same identifier enriches the whole
            // downstream scope. The request log observes the mapped result rather than the raw
            // exception, and the phase gate is not wired by this sample.
            app.UseServiceMantleCorrelationId();
            app.UseMiddleware<ReferenceRequestLoggingMiddleware>();
            app.UseServiceMantleProblemDetails();
        }

        // The fixed live and readiness endpoints are mapped if and only if the PostgreSQL startup
        // gate registered its options, exactly matching the capability registered in CreateBuilder.
        // No Phase Gate, management API, or installation status endpoint is wired here.
        if (app.Services.GetService<ReferencePostgreSqlStartupOptions>() is not null)
        {
            app.MapServiceMantleHealthEndpoints();
        }

        // No database creation, migration, setup, administrator provisioning, or management routes.
        app.MapGet("/", () => Results.Ok(new { service = "reference-service", status = "skeleton" }));
        return app;
    }

    /// <summary>Marks the sample's logging wiring as active for the composition seam.</summary>
    internal sealed class ReferenceLoggingRegistration;
}

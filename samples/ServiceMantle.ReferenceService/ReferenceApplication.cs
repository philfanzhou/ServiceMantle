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
using ServiceMantle.Persistence.EntityFrameworkCore;

namespace ServiceMantle.ReferenceService;

/// <summary>Composes the consumer-owned reference host without activating downstream capabilities.</summary>
public static class ReferenceApplication
{
    /// <summary>The service identity the composition uses, shared by the management key ring.</summary>
    internal static readonly ServiceId Service = ServiceId.Parse("reference-service");

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("reference-service"),
            InstanceId.Parse("reference-local"));
        // The switch is explicit and fixed before Build: a missing or unparsable value leaves the
        // ServiceMantle Serilog host, the sensitive Header registry, and the request log unwired.
        var logging = bool.TryParse(
            builder.Configuration[ReferenceLoggingDefaults.EnabledKey],
            out var loggingEnabled) && loggingEnabled;
        if (logging)
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

        // Fixed before Build, on the same shape as the other switches: only a value that parses to
        // true registers the ServiceMetrics publisher and the publishing snapshot decorator.
        var phaseMetrics = bool.TryParse(
            builder.Configuration[ReferenceTelemetryDefaults.PhaseMetricsEnabledKey],
            out var phaseMetricsEnabled) && phaseMetricsEnabled;
        if (phaseMetrics && postgresqlOptions is null)
        {
            // The authoritative phase comes from the gate's installation row; without the gate
            // there is nothing authoritative to publish, and no second phase source is invented.
            throw new InvalidOperationException(
                "The reference phase metric requires the PostgreSQL startup gate: '" +
                ReferenceTelemetryDefaults.PhaseMetricsEnabledKey + "' needs '" +
                ReferencePostgreSqlStartupOptions.EnabledKey + "' to be true.");
        }

        // Fixed before Build, on the same shape as the other switches: only a value that parses to
        // true registers the Prometheus scrape endpoint.
        var prometheus = bool.TryParse(
            builder.Configuration[ReferenceTelemetryDefaults.PrometheusEnabledKey],
            out var prometheusEnabled) && prometheusEnabled;
        if (prometheus && postgresqlOptions is null)
        {
            // The scrape is authorized by the management session the gate registers; without the
            // gate there is no session to authorize with, and anonymous scraping is never opened.
            throw new InvalidOperationException(
                "The reference Prometheus endpoint requires the management session: '" +
                ReferenceTelemetryDefaults.PrometheusEnabledKey + "' needs '" +
                ReferencePostgreSqlStartupOptions.EnabledKey + "' to be true.");
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
            if (phaseMetrics)
            {
                // The single publishing point: the host-owned ServiceMetrics publisher, fed by every
                // observation of the authoritative source. The source itself stays the registered
                // implementation; IServiceHealthSnapshotSource resolves to the transparent
                // decorator, so the phase gate and the health endpoints observe through it too.
                mantle.AddServiceMantleMetrics();
                builder.Services.AddSingleton<ReferencePostgreSqlHealthSnapshotSource>();
                builder.Services.AddSingleton<IServiceHealthSnapshotSource, ReferencePhaseMetricsSnapshotSource>();
            }
            else
            {
                builder.Services.AddSingleton<
                    IServiceHealthSnapshotSource,
                    ReferencePostgreSqlHealthSnapshotSource>();
            }
            mantle.AddServiceMantleHealthEndpoints();
            mantle.AddServiceReadinessContributor<ReferencePostgreSqlWorkspaceReadinessContributor>();
            // The management session rides the same gate: the external identity comes from the
            // deployment's operator directory, and the shared cookie key ring is persisted to the
            // same PostgreSQL target through the gate's context factory. The root key is a required
            // deployment input - a missing or short one fails here, before any database, network, or
            // file side effect, and there is no process-local random fallback.
            var management = ReferenceManagementOptions.Read(builder.Configuration);
            builder.Services.AddSingleton(management);
            builder.Services.AddDataProtection()
                .PersistKeysToServiceMantleEfCore<ReferencePostgreSqlDbContext>(Service, _ => management.RootKey);
            // The sensitive-header registry is required by the composed pipeline; the logging
            // switch may already have registered it with one denied name, which is kept.
            if (!logging)
            {
                mantle.AddSensitiveHeaders();
            }
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting();
            // Registered last among the data-protection configurators so its application name - the
            // discriminator two instances must agree on - is the effective one.
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1();
            mantle.AddServiceMantleManagementEntries();
            builder.Services.AddScoped<ReferenceOperatorCredentialAccessor>();
            builder.Services.AddScoped<IManagementIdentityProvider, ReferenceExternalManagementIdentityProvider>();
            if (prometheus)
            {
                // The authorized scrape endpoint: the path stays the package default /metrics, and
                // the authorization stays the existing admin policy - no new authentication scheme
                // or policy of the sample's own, and never anonymous scraping.
                builder.Services.AddSingleton<ReferencePrometheusRegistration>();
                mantle.AddOpenTelemetryPrometheusEndpoint(options =>
                {
                    options.Enabled = true;
                    options.AuthorizationPolicyName =
                        ServiceMantle.AspNetCore.Management.ManagementAuthorizationDefaults.AdminPolicyName;
                });
            }
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
            // With the PostgreSQL gate off, the placeholder stays the single readiness contributor
            // and never claims ready; no health endpoint, snapshot source, or management capability
            // is registered on this path, and login stays the fixed not-configured failure.
            builder.Services.AddSingleton<IServiceReadinessContributor, ReferenceReadinessContributor>();
            builder.Services.AddScoped<IManagementIdentityProvider, ExternalManagementIdentityPlaceholder>();
        }

        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        var loggingActive = app.Services.GetService<ReferenceLoggingRegistration>() is not null;
        // The PostgreSQL gate carries the composed ServiceMantle pipeline and the management
        // session: correlation, Problem Details, routing, the security-header baseline, the phase
        // gate, authentication, rate limiting, and authorization run in their fixed order, and the
        // three shared session entries are mapped beside the protected group, never inside it.
        if (app.Services.GetService<ReferencePostgreSqlStartupOptions>() is not null)
        {
            app.UseServiceMantlePipeline();
            if (loggingActive)
            {
                // Inside the composed pipeline the request log still observes the mapped result:
                // it runs downstream of Problem Details and upstream of the endpoints.
                app.UseMiddleware<ReferenceRequestLoggingMiddleware>();
            }

            app.MapServiceMantleManagementSession(ReferenceManagementLoginAdapter.AdaptAsync);
            app.MapServiceMantleHealthEndpoints();
            if (app.Services.GetService<ReferencePrometheusRegistration>() is not null)
            {
                // Mapped only when the switch authorized the registration above; calling the mapper
                // without that registration would itself throw at startup.
                app.MapServiceMantlePrometheusEndpoint();
            }
        }
        else if (loggingActive)
        {
            // Correlation stays outside Problem Details, so the same identifier enriches the whole
            // downstream scope. The request log observes the mapped result rather than the raw
            // exception, and the phase gate is not wired by this sample.
            app.UseServiceMantleCorrelationId();
            app.UseMiddleware<ReferenceRequestLoggingMiddleware>();
            app.UseServiceMantleProblemDetails();
        }

        // No database creation, migration, setup, administrator provisioning, or business
        // management routes of the sample's own.
        app.MapGet("/", () => Results.Ok(new { service = "reference-service", status = "skeleton" }));
        return app;
    }

    /// <summary>Marks the sample's logging wiring as active for the composition seam.</summary>
    internal sealed class ReferenceLoggingRegistration;
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Health;
using ServiceMantle.ReferenceService.Installation;
using ServiceMantle.ReferenceService.Logging;
using ServiceMantle.ReferenceService.Management;

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
        var databasePath = builder.Configuration["ReferenceService:DatabasePath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "reference.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ConnectionString;
        builder.Services.AddDbContext<ReferenceDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddSingleton<IServiceSettingDefinitionProvider, ReferenceSettingDefinitions>();
        builder.Services.AddSingleton(provider => new ServiceSettingDefinitionRegistry(
            provider.GetServices<IServiceSettingDefinitionProvider>()));
        builder.Services.AddScoped<IServiceSetupContributor, ReferenceSetupContributor>();
        builder.Services.AddSingleton<IServiceReadinessContributor, ReferenceReadinessContributor>();
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

        // No database creation, migration, setup, administrator provisioning, or management routes.
        app.MapGet("/", () => Results.Ok(new { service = "reference-service", status = "skeleton" }));
        return app;
    }

    /// <summary>Marks the sample's logging wiring as active for the composition seam.</summary>
    internal sealed class ReferenceLoggingRegistration;
}

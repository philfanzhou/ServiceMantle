using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceMantle.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers the mandatory-sanitizing ServiceMantle Serilog Console pipeline.</summary>
public static class ServiceMantleSerilogHostApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the ServiceMantle Serilog Console defaults to a host. Equivalent normalized duplicate
    /// registrations are idempotent; conflicting registrations fail when the host is started.
    /// Structured-property sanitization cannot be disabled through public options. Existing logging
    /// providers are removed so they cannot bypass the mandatory Console sanitization boundary.
    /// </summary>
    public static IHostApplicationBuilder AddServiceMantleSerilog(
        this IHostApplicationBuilder builder,
        Action<ServiceMantle.Serilog.ServiceMantleSerilogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceMantle.Serilog.ServiceMantleSerilogOptions();
        try
        {
            configure?.Invoke(options);
        }
        catch
        {
            throw new ServiceMantle.Serilog.ServiceMantleSerilogConfigurationException(
                "Configure",
                "serilog.configure_failed");
        }

        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ServiceMantle.Serilog.ServiceMantleSerilogMarker));
        var existingSerilogConfiguration = firstRegistration && HasSerilogConfiguration(builder.Services);
        builder.Services.AddSingleton(new ServiceMantle.Serilog.ServiceMantleSerilogRegistration(
            options,
            existingSerilogConfiguration));
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ServiceMantle.Serilog.ServiceMantleSerilogMarker>();
        builder.Services.TryAddSingleton<StructuredLogSanitizer>();
        builder.Services.TryAddSingleton<
            ServiceMantle.Serilog.IServiceMantleStructuredLogSanitizer,
            ServiceMantle.Serilog.ServiceMantleStructuredLogSanitizer>();
        builder.Services.TryAddSingleton<
            ServiceMantle.Serilog.IServiceMantleSerilogSinkFactory,
            ServiceMantle.Serilog.ServiceMantleConsoleSinkFactory>();
        builder.Services.TryAddSingleton<ServiceMantle.Serilog.ServiceMantleSerilogRuntime>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ILoggerProvider,
            ServiceMantle.Serilog.ServiceMantleSerilogLoggerProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantle.Serilog.ServiceMantleSerilogLifecycle>());
        return builder;
    }

    private static bool HasSerilogConfiguration(IServiceCollection services) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(global::Serilog.ILogger) ||
            IsSerilogType(descriptor.ImplementationType) ||
            IsSerilogType(descriptor.ImplementationInstance?.GetType()));

    private static bool IsSerilogType(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("Serilog", StringComparison.Ordinal) == true;
}

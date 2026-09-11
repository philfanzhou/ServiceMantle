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
        Action<ServiceMantle.Serilog.SerilogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceMantle.Serilog.SerilogOptions();
        try
        {
            configure?.Invoke(options);
        }
        catch
        {
            throw new ServiceMantle.Serilog.SerilogConfigurationException(
                "Configure",
                "serilog.configure_failed");
        }

        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ServiceMantle.Serilog.SerilogMarker));
        var existingSerilogConfiguration = firstRegistration && HasSerilogConfiguration(builder.Services);
        builder.Services.AddSingleton(new ServiceMantle.Serilog.SerilogRegistration(
            options,
            existingSerilogConfiguration));
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ServiceMantle.Serilog.SerilogMarker>();
        builder.Services.TryAddSingleton<StructuredLogSanitizer>(serviceProvider =>
            serviceProvider.GetService<IStructuredLogSanitizerProvider>()?.Sanitizer ??
            new StructuredLogSanitizer());
        builder.Services.TryAddSingleton<
            ServiceMantle.Serilog.ILogFieldSanitizer,
            ServiceMantle.Serilog.LogFieldSanitizer>();
        builder.Services.TryAddSingleton<
            ServiceMantle.Serilog.ISerilogSinkFactory,
            ServiceMantle.Serilog.ConsoleSinkFactory>();
        builder.Services.TryAddSingleton<ServiceMantle.Serilog.SerilogRuntime>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ILoggerProvider,
            ServiceMantle.Serilog.RuntimeLoggerProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantle.Serilog.SerilogLifecycle>());
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

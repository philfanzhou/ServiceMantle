using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Serilog;
using ServiceMantle.Serilog.GrafanaLoki;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers the isolated ServiceMantle Grafana Loki remote sink.</summary>
public static class ServiceMantleGrafanaLokiHostApplicationBuilderExtensions
{
    /// <summary>
    /// Adds a default-disabled Grafana Loki sink behind the mandatory ServiceMantle sanitizer.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">An optional action that explicitly enables and configures the sink.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The host must also register <c>AddServiceMantleSerilog</c>. Equivalent registrations are
    /// idempotent; invalid, incomplete, or conflicting settings fail when the host starts.
    /// </remarks>
    public static IHostApplicationBuilder AddServiceMantleGrafanaLoki(
        this IHostApplicationBuilder builder,
        Action<ServiceMantleGrafanaLokiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceMantleGrafanaLokiOptions();
        try
        {
            configure?.Invoke(options);
        }
        catch
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                "Configure",
                "loki.configure_failed");
        }

        var registration = new ServiceMantleGrafanaLokiRegistration(options);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ServiceMantleGrafanaLokiRegistration));
        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<ServiceMantleGrafanaLokiConfigurationProvider>();
        builder.Services.TryAddSingleton<ServiceMantleGrafanaLokiDiagnostics>();
        builder.Services.TryAddSingleton<ServiceMantleGrafanaLokiRuntime>();
        builder.Services.TryAddSingleton<
            IServiceMantleLokiHttpMessageHandlerFactory,
            ServiceMantleLokiHttpMessageHandlerFactory>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantleGrafanaLokiLifecycle>());

        if (options.Enabled)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<
                IServiceMantleSerilogSinkFactory,
                ServiceMantleGrafanaLokiSinkFactory>());
        }

        return builder;
    }
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Logging;
using ServiceMantle.OpenTelemetry;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the ServiceMantle-owned OpenTelemetry instrumentation set.
/// </summary>
public static class ServiceMantleOpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Adds explicitly controlled ASP.NET Core, <see cref="HttpClient"/>, and runtime
    /// instrumentation without adding an exporter.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">An optional action that selects the enabled instrumentation.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The OpenTelemetry resource contains only the service name, service version, and instance ID
    /// registered by <c>AddServiceMantle</c>. Equivalent repeated registrations are idempotent;
    /// invalid or conflicting registrations fail when the host starts, before activating the
    /// ServiceMantle instrumentation, including when framework DI resolves a provider early.
    /// </remarks>
    public static ServiceMantleBuilder AddOpenTelemetryInstrumentation(
        this ServiceMantleBuilder builder,
        Action<OpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new OpenTelemetryOptions();
        configure?.Invoke(options);
        var registration = new OpenTelemetryRegistration(
            options.Enabled,
            options.EnableAspNetCoreTracing,
            options.EnableHttpClientTracing,
            options.EnableRuntimeMetrics);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(OpenTelemetryRegistration));

        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService,
            OpenTelemetryRegistrationValidator>());

        if (!registration.Enabled)
        {
            return builder;
        }

        var logContext = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(ServiceLogContext))
            .Select(descriptor => descriptor.ImplementationInstance as ServiceLogContext)
            .Single(context => context is not null)!;
        var openTelemetry = builder.Services.AddOpenTelemetry();

        if (registration.EnableRuntimeMetrics)
        {
            // Framework DI/Options can create this provider before hosted StartingAsync runs.
            // Validate the final registrations before any of our instrumentation callbacks.
            builder.Services.ConfigureOpenTelemetryMeterProvider(static (services, _) =>
                OpenTelemetryRegistrationValidator.Validate(
                    services.GetServices<OpenTelemetryRegistration>()));
            openTelemetry.WithMetrics(metrics => metrics
                .SetResourceBuilder(CreateResource(logContext))
                .AddRuntimeInstrumentation());
        }

        if (registration.EnableAspNetCoreTracing || registration.EnableHttpClientTracing)
        {
            builder.Services.ConfigureOpenTelemetryTracerProvider(static (services, _) =>
                OpenTelemetryRegistrationValidator.Validate(
                    services.GetServices<OpenTelemetryRegistration>()));
            openTelemetry.WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(CreateResource(logContext));
                if (registration.EnableAspNetCoreTracing)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                if (registration.EnableHttpClientTracing)
                {
                    tracing.AddHttpClientInstrumentation();
                }
            });
        }

        return builder;
    }

    internal static ResourceBuilder CreateResource(ServiceLogContext context) =>
        ResourceBuilder.CreateEmpty().AddService(
            serviceName: context.ServiceName,
            serviceVersion: context.ServiceVersion,
            serviceInstanceId: context.InstanceId);
}

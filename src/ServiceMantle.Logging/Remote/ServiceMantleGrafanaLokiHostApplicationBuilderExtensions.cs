using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Configuration;
using ServiceMantle.Logging;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Logging.Remote;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers the isolated ServiceMantle Grafana Loki remote sink.</summary>
public static class ServiceMantleGrafanaLokiHostApplicationBuilderExtensions
{
    /// <summary>
    /// The fixed resolver entry name used by the setting-driven registration. It is non-secret;
    /// the Authorization value it resolves never appears in diagnostics.
    /// </summary>
    public const string SettingDrivenAuthorizationResolverName = "servicemantle-loki-settings";

    /// <summary>
    /// Registers the Grafana Loki sink driven by the activated setting snapshot's
    /// <c>loki.uri</c> / <c>loki.authorization</c> pair instead of explicit options.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="snapshot">The activated setting snapshot.</param>
    /// <param name="configure">
    /// An optional action over the remaining sink options (batching, labels, timeouts). The
    /// classification owns <c>Enabled</c>, <c>Endpoint</c>, and <c>AuthorizationHeaderResolverName</c>.
    /// </param>
    /// <returns>
    /// The classified state. A usable pair enables the sink under the existing explicit
    /// registration's validation rules; an empty pair keeps the sink disabled with zero registered
    /// activity; an unusable pair keeps the sink disabled and the consumer records the fixed
    /// <see cref="GrafanaLokiSettingState.Category"/> warning after startup.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The registration also contributes <see cref="GrafanaLokiSettingDefinitions"/> to the global
    /// setting catalog (definitions and the strict management-update combination validation). No
    /// <c>IConfiguration</c> is read and no value is written back.
    /// </para>
    /// <para>
    /// An enabled classification registers one <see cref="FixedRemoteLogAuthorizationResolver"/>
    /// answering <see cref="SettingDrivenAuthorizationResolverName"/> with the stored value, in
    /// memory only. Disabled and unusable classifications register no resolver, no sink factory
    /// replacement, and no lifecycle activity beyond the explicit entry's own default-disabled
    /// shape. Repeating this call with an equivalent snapshot is idempotent; the underlying
    /// explicit registration keeps its own idempotency and conflict rules.
    /// </para>
    /// </remarks>
    public static GrafanaLokiSettingState AddServiceMantleGrafanaLokiFromSettings(
        this IHostApplicationBuilder builder,
        ServiceSettingSnapshot snapshot,
        Action<GrafanaLokiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var state = GrafanaLokiSettingState.Classify(snapshot);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IServiceSettingDefinitionProvider,
            GrafanaLokiSettingDefinitions>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IServiceSettingCompositeValidator,
            GrafanaLokiSettingDefinitions>());

        if (state.Status == GrafanaLokiSettingStatus.Enabled &&
            !builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(IRemoteLogAuthorizationResolver) &&
                descriptor.ImplementationInstance is FixedRemoteLogAuthorizationResolver))
        {
            // Exactly one setting-driven resolver may exist: the sink requires exactly one
            // resolver, so a repeat with the same snapshot adds nothing and a repeat with a
            // different snapshot keeps the first value (and the sink's own conflict rules still
            // govern the registrations themselves).
            builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
                new FixedRemoteLogAuthorizationResolver(
                    SettingDrivenAuthorizationResolverName,
                    state.Authorization));
        }

        builder.AddServiceMantleGrafanaLoki(options =>
        {
            configure?.Invoke(options);
            if (state.Status == GrafanaLokiSettingStatus.Enabled)
            {
                options.Enabled = true;
                options.Endpoint = state.Endpoint;
                options.AuthorizationHeaderResolverName = SettingDrivenAuthorizationResolverName;
            }
            else
            {
                // The classification owns enablement: an unusable or empty pair keeps the sink
                // off regardless of what the optional configuration asked for.
                options.Enabled = false;
            }
        });

        return state;
    }

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
        Action<GrafanaLokiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new GrafanaLokiOptions();
        try
        {
            configure?.Invoke(options);
        }
        catch
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                "Configure",
                "loki.configure_failed");
        }

        var registration = new GrafanaLokiRegistration(options);
        var firstRegistration = !builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(GrafanaLokiRegistration));
        builder.Services.AddSingleton(registration);
        if (!firstRegistration)
        {
            return builder;
        }

        builder.Services.TryAddSingleton<GrafanaLokiConfigurationProvider>();
        builder.Services.TryAddSingleton<RemoteLogDeliveryDiagnostics>();
        builder.Services.TryAddSingleton<GrafanaLokiRuntime>();
        builder.Services.TryAddSingleton<
            ILokiHttpMessageHandlerFactory,
            LokiHttpMessageHandlerFactory>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            GrafanaLokiLifecycle>());

        if (options.Enabled)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<
                ISerilogSinkFactory,
                GrafanaLokiSinkFactory>());
        }

        return builder;
    }
}

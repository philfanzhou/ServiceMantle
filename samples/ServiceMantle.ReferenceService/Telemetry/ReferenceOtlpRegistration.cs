using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.Diagnostics;
using ServiceMantle.OpenTelemetry.Otlp;

namespace ServiceMantle.ReferenceService.Telemetry;

/// <summary>
/// Wires the sample to the public OTLP trace and metric exporters from explicit configuration.
/// </summary>
/// <remarks>
/// <para>
/// Every key lives under <c>ReferenceService:Telemetry:Otlp:</c> and is read once before the host is
/// built. With both signals' <c>Enabled</c> not parsing to <c>true</c>, nothing is registered: no
/// exporter, no authentication resolver, and no background export activity.
/// </para>
/// <para>
/// The sample only parses its own keys. <c>Protocol</c> accepts exactly <c>Grpc</c> or
/// <c>HttpProtobuf</c> (case sensitive) and defaults to <c>Grpc</c>; <c>Endpoint</c> must be an
/// absolute URI when present. Everything else - HTTPS enforcement, timeouts, batch bounds, header
/// legality - stays with the public package's own startup validation, and the sample exposes no
/// timeout or batch configuration of its own.
/// </para>
/// <para>
/// Authentication is header name and header value together or neither. When both are configured, a
/// sample-owned resolver answers only the lookup name <c>reference-otlp</c>, and the enabled
/// signals carry that name; the header value is a secret and never reaches a log, an exception
/// message, or a <c>ToString</c> of the sample's own making.
/// </para>
/// <para>
/// <c>AllowInsecureLoopbackForTesting</c> is honored only in the Development environment; every
/// other environment ignores it, leaving the loopback HTTP endpoint to the package's
/// <c>otlp.insecure_endpoint</c> rejection.
/// </para>
/// </remarks>
public static class ReferenceOtlpRegistrationExtensions
{
    /// <summary>The configuration prefix every OTLP key of this sample lives under.</summary>
    public const string Section = "ReferenceService:Telemetry:Otlp";

    /// <summary>The single non-secret lookup name the sample's resolver answers.</summary>
    public const string AuthenticationName = "reference-otlp";

    /// <summary>Adds the OTLP exporters for the signals the configuration explicitly enabled.</summary>
    /// <param name="mantle">The ServiceMantle builder, after the base instrumentation.</param>
    /// <param name="configuration">The configuration read before the host is built.</param>
    /// <param name="environment">The host environment that decides the loopback testing switch.</param>
    public static ServiceMantleBuilder AddReferenceOtlp(
        this ServiceMantleBuilder mantle,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(mantle);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var traces = ReadSignal(configuration, "Traces");
        var metrics = ReadSignal(configuration, "Metrics");
        if (traces is null && metrics is null)
        {
            // Nothing was enabled: no exporter call, no resolver, nothing registered.
            return mantle;
        }

        var headerName = configuration[$"{Section}:Authentication:HeaderName"];
        var headerValue = configuration[$"{Section}:Authentication:HeaderValue"];
        var hasHeaderName = !string.IsNullOrEmpty(headerName);
        var hasHeaderValue = !string.IsNullOrEmpty(headerValue);
        if (hasHeaderName != hasHeaderValue)
        {
            throw new InvalidOperationException(
                "The reference OTLP authentication needs both keys or neither: '" +
                $"{Section}:Authentication:HeaderName' and '{Section}:Authentication:HeaderValue'.");
        }

        // The loopback switch is a Development-only test affordance; every other environment
        // ignores it and the package rejects insecure endpoints itself.
        var allowLoopback = environment.IsDevelopment() &&
            bool.TryParse(configuration[$"{Section}:AllowInsecureLoopbackForTesting"], out var loopback) &&
            loopback;

        if (hasHeaderName && hasHeaderValue)
        {
            mantle.Services.AddSingleton<IRemoteTelemetryAuthenticationResolver>(
                new ConfigurationResolver(headerName!, headerValue!));
        }

        mantle.AddOpenTelemetryOtlpExporter(options =>
        {
            Apply(options.Traces, traces, hasHeaderName, allowLoopback);
            Apply(options.Metrics, metrics, hasHeaderName, allowLoopback);
        });
        return mantle;
    }

    private static (OtlpProtocol Protocol, Uri? Endpoint)? ReadSignal(
        IConfiguration configuration,
        string signal)
    {
        if (!bool.TryParse(configuration[$"{Section}:{signal}:Enabled"], out var enabled) || !enabled)
        {
            return null;
        }

        var protocolValue = configuration[$"{Section}:{signal}:Protocol"];
        OtlpProtocol protocol;
        if (protocolValue is null)
        {
            protocol = OtlpProtocol.Grpc;
        }
        else if (!string.Equals(protocolValue, nameof(OtlpProtocol.Grpc), StringComparison.Ordinal) &&
            !string.Equals(protocolValue, nameof(OtlpProtocol.HttpProtobuf), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The reference OTLP protocol key accepts only 'Grpc' or 'HttpProtobuf': '{Section}:{signal}:Protocol'.");
        }
        else
        {
            protocol = string.Equals(protocolValue, nameof(OtlpProtocol.Grpc), StringComparison.Ordinal)
                ? OtlpProtocol.Grpc
                : OtlpProtocol.HttpProtobuf;
        }

        var endpointValue = configuration[$"{Section}:{signal}:Endpoint"];
        if (endpointValue is not null &&
            !Uri.TryCreate(endpointValue, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"The reference OTLP endpoint must be an absolute URI: '{Section}:{signal}:Endpoint'.");
        }

        var endpoint = endpointValue is null
            ? null
            : Uri.TryCreate(endpointValue, UriKind.Absolute, out var parsed) ? parsed : null;
        return (protocol, endpoint);
    }

    private static void Apply(
        OtlpSignalOptions options,
        (OtlpProtocol Protocol, Uri? Endpoint)? input,
        bool authenticated,
        bool allowLoopback)
    {
        if (input is null)
        {
            return;
        }

        options.Enabled = true;
        options.Protocol = input.Value.Protocol;
        options.Endpoint = input.Value.Endpoint;
        options.AllowInsecureLoopbackForTesting = allowLoopback;
        if (authenticated)
        {
            options.AuthenticationHeaderName = AuthenticationName;
        }
    }

    /// <summary>
    /// Answers only the sample's lookup name with the configured header, whose value the sample
    /// never logs, echoes, or renders.
    /// </summary>
    private sealed class ConfigurationResolver(string headerName, string headerValue)
        : IRemoteTelemetryAuthenticationResolver
    {
        public bool TryResolve(string lookup, out RemoteTelemetryAuthenticationHeader? header)
        {
            if (!string.Equals(lookup, AuthenticationName, StringComparison.Ordinal))
            {
                header = null;
                return false;
            }

            header = new RemoteTelemetryAuthenticationHeader(headerName, headerValue);
            return true;
        }
    }
}

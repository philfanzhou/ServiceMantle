using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;

namespace ServiceMantle.ReferenceService.Telemetry;

/// <summary>Marks the sample's base telemetry wiring as active for the composition seam.</summary>
public sealed class ReferenceTelemetryRegistration;

/// <summary>
/// Wires the sample to the base instrumentation the public OpenTelemetry package already ships.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the sample's telemetry. It adds the ServiceMantle-owned ASP.NET Core,
/// <see cref="HttpClient"/>, and .NET runtime instrumentation and nothing else: no OTLP exporter, no
/// Prometheus endpoint, no <c>ServiceMetrics</c>, no health endpoint, and no service or
/// installation phase metric. It does not fabricate a phase, and it creates no remote export target.
/// </para>
/// <para>
/// The OpenTelemetry resource is whatever <c>AddServiceMantle</c> already registered - the service
/// name, the service version, and the instance ID. The sample adds no attribute of its own, so it
/// introduces no high-cardinality dimension and reads no Header, body, query, or connection field.
/// </para>
/// <para>
/// The host owns provider and listener start, stop, and disposal.
/// </para>
/// </remarks>
public static class ReferenceTelemetryRegistrationExtensions
{
    /// <summary>Adds the base instrumentation when the explicit switch parses to <c>true</c>.</summary>
    /// <param name="mantle">The ServiceMantle builder, after the service identity is registered.</param>
    /// <param name="configuration">The configuration read before the host is built.</param>
    /// <returns>
    /// <c>true</c> when the switch authorized the wiring; otherwise <c>false</c>, leaving every
    /// ServiceMantle-owned provider unregistered.
    /// </returns>
    public static bool AddReferenceTelemetry(
        this ServiceMantleBuilder mantle,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(mantle);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!bool.TryParse(configuration[ReferenceTelemetryDefaults.EnabledKey], out var enabled) ||
            !enabled)
        {
            return false;
        }

        mantle.Services.AddSingleton<ReferenceTelemetryRegistration>();
        // The fixed public set: ASP.NET Core tracing, HttpClient tracing, and runtime metrics. The
        // sample deliberately adds no options system of its own on top of it.
        mantle.AddOpenTelemetryInstrumentation();
        return true;
    }
}

namespace ServiceMantle.OpenTelemetry;

/// <summary>
/// Configures the explicitly enabled OpenTelemetry instrumentation owned by ServiceMantle.
/// </summary>
public sealed class ServiceMantleOpenTelemetryOptions
{
    /// <summary>
    /// Gets or sets whether ServiceMantle creates OpenTelemetry providers for this registration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether incoming ASP.NET Core requests are traced.
    /// </summary>
    public bool EnableAspNetCoreTracing { get; set; } = true;

    /// <summary>
    /// Gets or sets whether outgoing <see cref="HttpClient"/> requests are traced.
    /// </summary>
    public bool EnableHttpClientTracing { get; set; } = true;

    /// <summary>
    /// Gets or sets whether .NET runtime metrics are collected.
    /// </summary>
    public bool EnableRuntimeMetrics { get; set; } = true;
}

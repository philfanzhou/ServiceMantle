namespace ServiceMantle.OpenTelemetry.Prometheus;

/// <summary>Configures the authorized ServiceMantle Prometheus scraping endpoint.</summary>
public sealed class ServiceMantlePrometheusOptions
{
    /// <summary>Gets or sets whether the Prometheus exporter and endpoint are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the single-segment absolute endpoint path.</summary>
    public string EndpointPath { get; set; } = ServiceMantlePrometheusDefaults.EndpointPath;

    /// <summary>Gets or sets the existing authorization policy required by every scrape.</summary>
    /// <remarks>This package does not create an authentication scheme or authorization policy.</remarks>
    public string? AuthorizationPolicyName { get; set; }
}

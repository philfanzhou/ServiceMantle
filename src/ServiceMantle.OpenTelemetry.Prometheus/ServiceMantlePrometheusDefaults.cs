namespace ServiceMantle.OpenTelemetry.Prometheus;

/// <summary>Defines the fixed ServiceMantle Prometheus endpoint limits.</summary>
public static class ServiceMantlePrometheusDefaults
{
    /// <summary>Gets the default scrape endpoint path.</summary>
    public const string EndpointPath = "/metrics";

    /// <summary>Gets the maximum uncompressed scrape response size.</summary>
    public const int MaximumResponseSizeBytes = 4 * 1024 * 1024;

    /// <summary>Gets the maximum number of concurrent scrape requests.</summary>
    public const int MaximumConcurrentScrapes = 4;
}

namespace ServiceMantle.ReferenceService.Telemetry;

/// <summary>Marks the sample's Prometheus scrape wiring as active for the composition seam.</summary>
/// <remarks>
/// The marker exists so <c>ReferenceApplication.Build</c> maps the scrape endpoint exactly when the
/// explicit switch authorized its registration during <c>CreateBuilder</c>, without reading the
/// configuration a second time.
/// </remarks>
public sealed class ReferencePrometheusRegistration;

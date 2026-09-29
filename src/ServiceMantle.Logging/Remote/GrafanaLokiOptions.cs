namespace ServiceMantle.Logging.Remote;

/// <summary>Configures the isolated ServiceMantle Grafana Loki sink.</summary>
public sealed class GrafanaLokiOptions
{
    /// <summary>Gets or sets whether the remote sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the Loki base endpoint.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Gets or sets whether loopback HTTP is allowed exclusively for tests.</summary>
    public bool AllowInsecureLoopbackForTesting { get; set; }

    /// <summary>
    /// Gets or sets whether plain-HTTP Loki endpoints are accepted on any host, not only loopback.
    /// </summary>
    /// <remarks>
    /// The default is <c>false</c>: only HTTPS endpoints are accepted, plus loopback HTTP when
    /// <see cref="AllowInsecureLoopbackForTesting"/> is set. Setting this switch is an explicit
    /// acceptance that the complete log content and any configured Authorization header travel
    /// in cleartext to that endpoint. ServiceMantle does not verify that the endpoint is
    /// reachable only over a trusted network; use HTTPS whenever the path crosses an untrusted
    /// network. No hostname or address shape is treated as implicitly trusted.
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Gets or sets the non-secret name passed to the authorization header resolver. When unset,
    /// no Authorization header is resolved or sent.
    /// </summary>
    public string? AuthorizationHeaderResolverName { get; set; }

    /// <summary>Gets or sets the maximum events per request.</summary>
    public int BatchSize { get; set; } = GrafanaLokiDefaults.BatchSize;

    /// <summary>Gets or sets the maximum events held by the upstream in-memory queue.</summary>
    public int QueueLimit { get; set; } = GrafanaLokiDefaults.QueueLimit;

    /// <summary>Gets or sets the maximum delay between batches.</summary>
    public TimeSpan FlushPeriod { get; set; } = GrafanaLokiDefaults.FlushPeriod;

    /// <summary>Gets or sets the maximum shutdown drain duration.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } =
        GrafanaLokiDefaults.ShutdownDrainTimeout;

    /// <summary>Returns only fixed, non-sensitive configuration metadata.</summary>
    public override string ToString() => "GrafanaLokiOptions";
}

/// <summary>Defines the fixed ServiceMantle Grafana Loki defaults and bounds.</summary>
public static class GrafanaLokiDefaults
{
    /// <summary>The default batch size.</summary>
    public const int BatchSize = 100;

    /// <summary>The default queue limit.</summary>
    public const int QueueLimit = 1_000;

    /// <summary>The default flush period.</summary>
    public static TimeSpan FlushPeriod { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The default shutdown drain timeout.</summary>
    public static TimeSpan ShutdownDrainTimeout { get; } = TimeSpan.FromSeconds(5);
}

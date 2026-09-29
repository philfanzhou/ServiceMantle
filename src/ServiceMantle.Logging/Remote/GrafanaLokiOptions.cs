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

    /// <summary>
    /// Gets or sets the explicit fixed stream labels attached to every Loki stream this sink emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When unset (the default), no consumer labels are added and the stream labels keep their
    /// existing shape: the sink-owned <c>level</c> label only. Label values come exclusively from
    /// this explicit configuration; log event properties are never promoted to labels.
    /// </para>
    /// <para>
    /// Keys must match <c>^[A-Za-z_][A-Za-z0-9_]*$</c>, be 1-128 characters long, and not collide
    /// with the sink-reserved keys in <see cref="GrafanaLokiDefaults.ReservedLabelKeys"/>
    /// (currently <c>level</c>). Values must be 1-1024 characters long and contain no control
    /// characters. At most <see cref="GrafanaLokiDefaults.MaxLabelCount"/> labels are accepted.
    /// Invalid label configuration fails when the host starts, without echoing any key or value.
    /// </para>
    /// <para>
    /// Callers are responsible for choosing low-cardinality, non-sensitive label values:
    /// ServiceMantle does not verify that values contain no secrets, and Loki indexes every
    /// distinct label value set as a separate stream, so high-cardinality values multiply streams
    /// and degrade querying. Evaluate label changes against existing Grafana queries and alerts.
    /// </para>
    /// </remarks>
    public IDictionary<string, string>? Labels { get; set; }

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

    /// <summary>The maximum fixed label key length.</summary>
    public const int MaxLabelKeyLength = 128;

    /// <summary>The maximum fixed label value length.</summary>
    public const int MaxLabelValueLength = 1_024;

    /// <summary>The maximum number of fixed labels.</summary>
    public const int MaxLabelCount = 8;

    /// <summary>
    /// The fixed label keys reserved by the sink itself. The reserved set is exactly the labels
    /// the sink wiring owns: <c>level</c> via the level-as-label mode, with no property-derived,
    /// trace, or span labels configured.
    /// </summary>
    public static IReadOnlySet<string> ReservedLabelKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "level" };
}

namespace ServiceMantle.Serilog.GrafanaLoki;

/// <summary>Configures the isolated ServiceMantle Grafana Loki sink.</summary>
public sealed class ServiceMantleGrafanaLokiOptions
{
    /// <summary>Gets or sets whether the remote sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the Loki base endpoint.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Gets or sets whether loopback HTTP is allowed exclusively for tests.</summary>
    public bool AllowInsecureLoopbackForTesting { get; set; }

    /// <summary>Gets or sets the non-secret name passed to the authorization header resolver.</summary>
    public string? AuthorizationHeaderResolverName { get; set; }

    /// <summary>Gets or sets the maximum events per request.</summary>
    public int BatchSize { get; set; } = ServiceMantleGrafanaLokiDefaults.BatchSize;

    /// <summary>Gets or sets the maximum events held by the upstream in-memory queue.</summary>
    public int QueueLimit { get; set; } = ServiceMantleGrafanaLokiDefaults.QueueLimit;

    /// <summary>Gets or sets the maximum delay between batches.</summary>
    public TimeSpan FlushPeriod { get; set; } = ServiceMantleGrafanaLokiDefaults.FlushPeriod;

    /// <summary>Gets or sets the maximum shutdown drain duration.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } =
        ServiceMantleGrafanaLokiDefaults.ShutdownDrainTimeout;

    /// <summary>Returns only fixed, non-sensitive configuration metadata.</summary>
    public override string ToString() => "ServiceMantleGrafanaLokiOptions";
}

/// <summary>Defines the fixed ServiceMantle Grafana Loki defaults and bounds.</summary>
public static class ServiceMantleGrafanaLokiDefaults
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

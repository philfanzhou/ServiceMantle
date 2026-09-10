namespace ServiceMantle.Serilog;

/// <summary>Configures the bounded ServiceMantle Serilog host and Console pipeline.</summary>
/// <remarks>
/// Structured-property sanitization is mandatory and intentionally has no disable or bypass option.
/// Message-template literal text remains subject to the free-text non-guarantee documented by
/// ServiceMantle.
/// </remarks>
public sealed class SerilogOptions
{
    /// <summary>Gets or sets the minimum Serilog level name.</summary>
    public string MinimumLevel { get; set; } = SerilogDefaults.MinimumLevel;

    /// <summary>Gets or sets the Console output template.</summary>
    public string OutputTemplate { get; set; } = SerilogDefaults.OutputTemplate;

    /// <summary>
    /// Gets or sets the deterministic enricher names. The first release supports only
    /// <c>FromLogContext</c>.
    /// </summary>
    public IEnumerable<string> EnricherNames { get; set; } =
        SerilogDefaults.EnricherNames;

    /// <summary>Gets or sets the maximum time allowed for one-time pipeline flushing.</summary>
    public TimeSpan FlushTimeout { get; set; } = SerilogDefaults.FlushTimeout;
}

/// <summary>Defines the deterministic ServiceMantle Serilog defaults.</summary>
public static class SerilogDefaults
{
    /// <summary>The default minimum level.</summary>
    public const string MinimumLevel = "Information";

    /// <summary>The default Console output template.</summary>
    public const string OutputTemplate =
        "[{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}";

    /// <summary>The default immutable enricher set.</summary>
    public static IReadOnlyList<string> EnricherNames { get; } =
        Array.AsReadOnly(["FromLogContext"]);

    /// <summary>The default upper bound for one-time flushing.</summary>
    public static TimeSpan FlushTimeout { get; } = TimeSpan.FromSeconds(2);
}

using Microsoft.Extensions.Logging;

namespace ServiceMantle.Serilog;

/// <summary>Configures the bounded ServiceMantle Serilog host and Console pipeline.</summary>
/// <remarks>
/// Structured-property sanitization is mandatory and intentionally has no disable or bypass option.
/// Message-template literal text remains subject to the free-text non-guarantee documented by
/// ServiceMantle.
/// </remarks>
public sealed class SerilogOptions
{
    /// <summary>Gets or sets the minimum level in Microsoft.Extensions.Logging vocabulary.</summary>
    public LogLevel MinimumLevel { get; set; } = SerilogDefaults.MinimumLevel;

    /// <summary>
    /// Gets or sets the Console output template. This is a Serilog template-syntax escape hatch:
    /// the syntax belongs to the current sink implementation, and its portability is not
    /// guaranteed when a different sink implementation is installed.
    /// </summary>
    public string OutputTemplate { get; set; } = SerilogDefaults.OutputTemplate;

    /// <summary>
    /// Gets or sets whether Microsoft.Extensions.Logging scopes are propagated to the Serilog
    /// log context. Scope propagation is enabled by default.
    /// </summary>
    public bool IncludeScopes { get; set; } = SerilogDefaults.IncludeScopes;

    /// <summary>Gets or sets the maximum time allowed for one-time pipeline flushing.</summary>
    public TimeSpan FlushTimeout { get; set; } = SerilogDefaults.FlushTimeout;
}

/// <summary>Defines the deterministic ServiceMantle Serilog defaults.</summary>
public static class SerilogDefaults
{
    /// <summary>The default minimum level.</summary>
    public const LogLevel MinimumLevel = LogLevel.Information;

    /// <summary>The default Console output template.</summary>
    public const string OutputTemplate =
        "[{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}";

    /// <summary>The default scope propagation.</summary>
    public const bool IncludeScopes = true;

    /// <summary>The default upper bound for one-time flushing.</summary>
    public static TimeSpan FlushTimeout { get; } = TimeSpan.FromSeconds(2);
}

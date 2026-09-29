using Microsoft.Extensions.Logging;

namespace ServiceMantle.Logging.Pipeline;

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
    /// Gets or sets optional per-category minimum level overrides in Microsoft.Extensions.Logging
    /// vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When unset (the default), no overrides apply and the global <see cref="MinimumLevel"/>
    /// governs every category, matching the pre-override behavior exactly. Keys are logger
    /// category prefixes: a key matches a category when it equals the category or is a prefix of
    /// it ending on a dot boundary, and when several keys match, the longest key wins
    /// (<c>Microsoft</c> matches <c>Microsoft.AspNetCore.Hosting</c>; <c>Microsoft.AspNetCor</c>
    /// does not match anything under <c>Microsoft.AspNetCore</c>). Matching is case-sensitive.
    /// </para>
    /// <para>
    /// Keys are trimmed, must be non-empty and at most
    /// <see cref="SerilogDefaults.MaximumLevelOverrideKeyLength"/> characters long, and values
    /// must be defined log levels other than <see cref="LogLevel.None"/>. Keys that differ only
    /// in surrounding whitespace are the same key: repeating it with the same level is accepted
    /// idempotently, while repeating it with different levels fails deterministically when the
    /// Host starts, regardless of dictionary ordering. Invalid overrides fail when the Host
    /// starts without echoing the submitted keys or level values. Overrides only filter events;
    /// they do not change sanitization, message content, or delivery semantics, and Console and
    /// remote sinks observe the same filtered result. Third-party logging providers outside
    /// this pipeline are not governed by these overrides.
    /// </para>
    /// </remarks>
    public IDictionary<string, LogLevel>? MinimumLevelOverrides { get; set; }

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

    /// <summary>The maximum category key length accepted by per-category level overrides.</summary>
    public const int MaximumLevelOverrideKeyLength = 256;

    /// <summary>The default Console output template.</summary>
    public const string OutputTemplate =
        "[{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}";

    /// <summary>The default scope propagation.</summary>
    public const bool IncludeScopes = true;

    /// <summary>The default upper bound for one-time flushing.</summary>
    public static TimeSpan FlushTimeout { get; } = TimeSpan.FromSeconds(2);
}

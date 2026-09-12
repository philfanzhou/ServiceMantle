using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ServiceMantle.Serilog;

internal sealed class SerilogRegistration
{
    internal SerilogRegistration(
        SerilogOptions options,
        bool existingSerilogConfiguration)
    {
        Options = options;
        ExistingSerilogConfiguration = existingSerilogConfiguration;
    }

    internal SerilogOptions Options { get; }

    internal bool ExistingSerilogConfiguration { get; }
}

internal sealed class SerilogConfiguration
{
    private static readonly TimeSpan MaximumFlushTimeout = TimeSpan.FromSeconds(30);

    private SerilogConfiguration(
        LogEventLevel minimumLevel,
        string outputTemplate,
        bool includeScopes,
        TimeSpan flushTimeout)
    {
        MinimumLevel = minimumLevel;
        OutputTemplate = outputTemplate;
        IncludeScopes = includeScopes;
        FlushTimeout = flushTimeout;
    }

    internal LogEventLevel MinimumLevel { get; }

    internal string OutputTemplate { get; }

    internal bool IncludeScopes { get; }

    internal TimeSpan FlushTimeout { get; }

    internal static SerilogConfiguration Resolve(
        IEnumerable<SerilogRegistration> registrations)
    {
        SerilogRegistration[] materialized;
        try
        {
            materialized = registrations.ToArray();
        }
        catch
        {
            throw Failure("Registrations", "serilog.registrations_invalid");
        }

        if (materialized.Length == 0)
        {
            throw Failure("Registrations", "serilog.registrations_missing");
        }

        if (materialized.Any(registration => registration.ExistingSerilogConfiguration))
        {
            throw Failure("ConsoleSink", "serilog.console_sink_conflict");
        }

        var normalized = materialized.Select(registration => Normalize(registration.Options)).ToArray();
        var first = normalized[0];
        foreach (var candidate in normalized.Skip(1))
        {
            if (!string.Equals(first.OutputTemplate, candidate.OutputTemplate, StringComparison.Ordinal))
            {
                throw Failure("ConsoleSink", "serilog.console_sink_conflict");
            }

            if (first.MinimumLevel != candidate.MinimumLevel ||
                first.FlushTimeout != candidate.FlushTimeout ||
                first.IncludeScopes != candidate.IncludeScopes)
            {
                throw Failure("Registrations", "serilog.registration_conflict");
            }
        }

        return first;
    }

    private static SerilogConfiguration Normalize(SerilogOptions options)
    {
        // LogLevel.None is rejected outright rather than interpreted as an implicit off switch:
        // silencing the pipeline is an explicit configuration decision, not a level value.
        if (options is null ||
            !Enum.IsDefined(options.MinimumLevel) ||
            options.MinimumLevel == LogLevel.None)
        {
            throw Failure("MinimumLevel", "serilog.minimum_level_invalid");
        }

        if (string.IsNullOrWhiteSpace(options.OutputTemplate))
        {
            throw Failure("OutputTemplate", "serilog.output_template_invalid");
        }

        try
        {
            ValidateOutputTemplate(options.OutputTemplate);
            _ = new MessageTemplateTextFormatter(options.OutputTemplate);
        }
        catch
        {
            throw Failure("OutputTemplate", "serilog.output_template_invalid");
        }

        if (options.FlushTimeout <= TimeSpan.Zero || options.FlushTimeout > MaximumFlushTimeout)
        {
            throw Failure("FlushTimeout", "serilog.flush_timeout_invalid");
        }

        return new SerilogConfiguration(
            ToSerilogLevel(options.MinimumLevel),
            options.OutputTemplate,
            options.IncludeScopes,
            options.FlushTimeout);
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => throw Failure("MinimumLevel", "serilog.minimum_level_invalid"),
    };

    private static void ValidateOutputTemplate(string outputTemplate)
    {
        for (var index = 0; index < outputTemplate.Length; index++)
        {
            if (outputTemplate[index] == '{')
            {
                if (index + 1 < outputTemplate.Length && outputTemplate[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var closingBrace = outputTemplate.IndexOf('}', index + 1);
                if (closingBrace < 0 || closingBrace == index + 1 ||
                    outputTemplate.AsSpan(index + 1, closingBrace - index - 1).Contains('{'))
                {
                    throw Failure("OutputTemplate", "serilog.output_template_invalid");
                }

                index = closingBrace;
                continue;
            }

            if (outputTemplate[index] == '}')
            {
                if (index + 1 < outputTemplate.Length && outputTemplate[index + 1] == '}')
                {
                    index++;
                    continue;
                }

                throw Failure("OutputTemplate", "serilog.output_template_invalid");
            }
        }
    }

    private static SerilogConfigurationException Failure(
        string fieldName,
        string errorCode) =>
        new(fieldName, errorCode);
}

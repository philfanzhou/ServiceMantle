using Serilog.Events;
using Serilog.Formatting.Display;

namespace ServiceMantle.Serilog;

internal sealed class ServiceMantleSerilogRegistration
{
    internal ServiceMantleSerilogRegistration(
        ServiceMantleSerilogOptions options,
        bool existingSerilogConfiguration)
    {
        Options = options;
        ExistingSerilogConfiguration = existingSerilogConfiguration;
    }

    internal ServiceMantleSerilogOptions Options { get; }

    internal bool ExistingSerilogConfiguration { get; }
}

internal sealed class ServiceMantleSerilogConfiguration
{
    private static readonly TimeSpan MaximumFlushTimeout = TimeSpan.FromSeconds(30);

    private ServiceMantleSerilogConfiguration(
        LogEventLevel minimumLevel,
        string outputTemplate,
        string[] enricherNames,
        TimeSpan flushTimeout)
    {
        MinimumLevel = minimumLevel;
        OutputTemplate = outputTemplate;
        EnricherNames = enricherNames;
        FlushTimeout = flushTimeout;
    }

    internal LogEventLevel MinimumLevel { get; }

    internal string OutputTemplate { get; }

    internal IReadOnlyList<string> EnricherNames { get; }

    internal TimeSpan FlushTimeout { get; }

    internal static ServiceMantleSerilogConfiguration Resolve(
        IEnumerable<ServiceMantleSerilogRegistration> registrations)
    {
        ServiceMantleSerilogRegistration[] materialized;
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
                !first.EnricherNames.SequenceEqual(candidate.EnricherNames, StringComparer.Ordinal))
            {
                throw Failure("Registrations", "serilog.registration_conflict");
            }
        }

        return first;
    }

    private static ServiceMantleSerilogConfiguration Normalize(ServiceMantleSerilogOptions options)
    {
        if (options is null ||
            string.IsNullOrWhiteSpace(options.MinimumLevel) ||
            !Enum.TryParse<LogEventLevel>(options.MinimumLevel.Trim(), ignoreCase: true, out var minimumLevel) ||
            !Enum.IsDefined(minimumLevel))
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

        string[] enricherNames;
        try
        {
            enricherNames = options.EnricherNames
                .Select(name => name?.Trim() ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            throw Failure("EnricherNames", "serilog.enricher_names_invalid");
        }

        if (enricherNames.Any(name =>
                !string.Equals(name, "FromLogContext", StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure("EnricherNames", "serilog.enricher_names_invalid");
        }

        if (options.FlushTimeout <= TimeSpan.Zero || options.FlushTimeout > MaximumFlushTimeout)
        {
            throw Failure("FlushTimeout", "serilog.flush_timeout_invalid");
        }

        return new ServiceMantleSerilogConfiguration(
            minimumLevel,
            options.OutputTemplate,
            enricherNames.Select(_ => "FromLogContext").ToArray(),
            options.FlushTimeout);
    }

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

    private static ServiceMantleSerilogConfigurationException Failure(
        string fieldName,
        string errorCode) =>
        new(fieldName, errorCode);
}

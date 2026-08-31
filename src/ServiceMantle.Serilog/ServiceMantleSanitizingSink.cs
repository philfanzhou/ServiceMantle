using System.Collections;
using global::Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceMantle.Logging;

namespace ServiceMantle.Serilog;

internal interface IServiceMantleStructuredLogSanitizer
{
    IReadOnlyDictionary<string, object?> SanitizeFields(
        IEnumerable<KeyValuePair<string, object?>> fields);
}

internal sealed class ServiceMantleStructuredLogSanitizer(StructuredLogSanitizer sanitizer)
    : IServiceMantleStructuredLogSanitizer
{
    public IReadOnlyDictionary<string, object?> SanitizeFields(
        IEnumerable<KeyValuePair<string, object?>> fields) =>
        sanitizer.SanitizeFields(fields);
}

internal interface IServiceMantleSerilogSinkFactory
{
    ILogEventSink Create(
        ServiceMantleSerilogConfiguration configuration,
        IServiceMantleStructuredLogSanitizer sanitizer);
}

internal sealed class ServiceMantleConsoleSinkFactory : IServiceMantleSerilogSinkFactory
{
    public ILogEventSink Create(
        ServiceMantleSerilogConfiguration configuration,
        IServiceMantleStructuredLogSanitizer sanitizer)
    {
        var consoleLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(outputTemplate: configuration.OutputTemplate)
            .CreateLogger();
        return new ServiceMantleSanitizingSink(sanitizer, consoleLogger);
    }
}

internal sealed class ServiceMantleSanitizingSink(
    IServiceMantleStructuredLogSanitizer sanitizer,
    global::Serilog.ILogger innerLogger) : ILogEventSink, IDisposable
{
    private int disposed;

    public void Emit(LogEvent logEvent)
    {
        IReadOnlyDictionary<string, object?> sanitized;
        try
        {
            var fields = logEvent.Properties
                .Select(property => new KeyValuePair<string, object?>(
                    property.Key,
                    Unwrap(property.Value)))
                .ToList();
            if (logEvent.Exception is not null &&
                !logEvent.Properties.ContainsKey("Exception"))
            {
                fields.Add(new("Exception", logEvent.Exception));
            }

            sanitized = sanitizer.SanitizeFields(fields);
        }
        catch
        {
            sanitized = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SanitizationFailure"] = StructuredLogSanitizer.SanitizationFailed
            };
        }

        LogEvent safeEvent;
        try
        {
            safeEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                exception: null,
                logEvent.MessageTemplate,
                sanitized.Select(field => new LogEventProperty(
                    field.Key,
                    Wrap(field.Value))));
        }
        catch
        {
            safeEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                exception: null,
                logEvent.MessageTemplate,
                [new LogEventProperty(
                    "SanitizationFailure",
                    new ScalarValue(StructuredLogSanitizer.SanitizationFailed))]);
        }

        innerLogger.Write(safeEvent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && innerLogger is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static object? Unwrap(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => scalar.Value,
        SequenceValue sequence => sequence.Elements.Select(Unwrap).ToArray(),
        StructureValue structure => structure.Properties.ToDictionary(
            property => property.Name,
            property => Unwrap(property.Value),
            StringComparer.Ordinal),
        DictionaryValue dictionary => UnwrapDictionary(dictionary),
        _ => StructuredLogSanitizer.SanitizationFailed
    };

    private static IDictionary UnwrapDictionary(DictionaryValue dictionary)
    {
        var output = new Hashtable();
        foreach (var element in dictionary.Elements)
        {
            output[Unwrap(element.Key) ?? StructuredLogSanitizer.SanitizationFailed] =
                Unwrap(element.Value);
        }

        return output;
    }

    private static LogEventPropertyValue Wrap(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> dictionary => new StructureValue(
            dictionary.Select(field => new LogEventProperty(field.Key, Wrap(field.Value)))),
        IReadOnlyList<object?> sequence => new SequenceValue(sequence.Select(Wrap)),
        _ => new ScalarValue(value)
    };
}

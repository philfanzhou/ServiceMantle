namespace ServiceMantle.Logging;

internal interface IStructuredLogSanitizerProvider
{
    StructuredLogSanitizer Sanitizer { get; }
}

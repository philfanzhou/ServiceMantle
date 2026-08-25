namespace ServiceMantle.Logging;

/// <summary>
/// Marks a consumer-owned type whose instances must never be destructured into log output.
/// </summary>
public interface ISensitiveLogValue
{
}

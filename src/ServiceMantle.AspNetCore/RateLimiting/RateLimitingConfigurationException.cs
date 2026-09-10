namespace ServiceMantle.AspNetCore.RateLimiting;

/// <summary>Reports an invalid or conflicting ServiceMantle rate-limiting registration.</summary>
public sealed class RateLimitingConfigurationException : InvalidOperationException
{
    internal RateLimitingConfigurationException(string fieldName, bool conflicting)
        : base(conflicting
            ? "ServiceMantle rate limiting was registered with conflicting settings."
            : $"The ServiceMantle rate-limiting setting '{fieldName}' is invalid.")
    {
        FieldName = fieldName;
    }

    /// <summary>Gets the safe name of the invalid configuration field.</summary>
    public string FieldName { get; }
}

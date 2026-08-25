namespace ServiceMantle.Configuration;

/// <summary>
/// Indicates that a setting catalog cannot be constructed safely.
/// </summary>
public sealed class ServiceSettingDefinitionException : Exception
{
    internal ServiceSettingDefinitionException(string? key, string errorCode)
        : base(key is null
            ? $"The service setting catalog is invalid ({errorCode})."
            : $"The service setting definition '{key}' is invalid ({errorCode}).")
    {
        Key = key;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the normalized setting key when the failure belongs to one definition.
    /// </summary>
    public string? Key { get; }

    /// <summary>
    /// Gets the stable, non-secret failure classification.
    /// </summary>
    public string ErrorCode { get; }
}

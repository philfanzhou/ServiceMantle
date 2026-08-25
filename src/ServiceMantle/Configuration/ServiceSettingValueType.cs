namespace ServiceMantle.Configuration;

/// <summary>
/// Identifies the supported persisted representation of a service setting.
/// </summary>
public enum ServiceSettingValueType
{
    /// <summary>A UTF-16 string value.</summary>
    String,

    /// <summary>An invariant-culture decimal number.</summary>
    Number,

    /// <summary>A Boolean value represented by true or false.</summary>
    Boolean,

    /// <summary>A syntactically valid JSON value.</summary>
    Json
}

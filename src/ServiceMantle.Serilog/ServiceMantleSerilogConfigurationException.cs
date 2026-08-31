namespace ServiceMantle.Serilog;

/// <summary>Reports a safe, startup-time ServiceMantle Serilog configuration failure.</summary>
public sealed class ServiceMantleSerilogConfigurationException : InvalidOperationException
{
    internal ServiceMantleSerilogConfigurationException(string fieldName, string errorCode)
        : base($"ServiceMantle Serilog configuration failed (Field={fieldName}, ErrorCode={errorCode}).")
    {
        FieldName = fieldName;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable public option field name.</summary>
    public string FieldName { get; }

    /// <summary>Gets the stable configuration error code.</summary>
    public string ErrorCode { get; }
}

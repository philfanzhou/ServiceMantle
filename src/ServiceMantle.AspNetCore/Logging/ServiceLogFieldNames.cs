namespace ServiceMantle.Logging;

/// <summary>
/// Defines the stable structured field names emitted by <see cref="ServiceLogContext"/>.
/// </summary>
public static class ServiceLogFieldNames
{
    /// <summary>
    /// The stable service identity field.
    /// </summary>
    public const string ServiceName = "ServiceName";

    /// <summary>
    /// The running service version field.
    /// </summary>
    public const string ServiceVersion = "ServiceVersion";

    /// <summary>
    /// The running instance identity field.
    /// </summary>
    public const string InstanceId = "InstanceId";

    /// <summary>
    /// The per-request Correlation ID field.
    /// </summary>
    /// <remarks>
    /// Only the trusted request scope opened by the ServiceMantle Correlation ID middleware writes
    /// this field; it can never be supplied through <see cref="ServiceLogContext.BeginScope"/>
    /// extension fields.
    /// </remarks>
    public const string CorrelationId = "CorrelationId";
}

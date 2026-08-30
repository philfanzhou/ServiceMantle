namespace ServiceMantle.Http;

/// <summary>
/// Defines the stable HTTP header names owned by ServiceMantle.
/// </summary>
public static class ServiceMantleHeaderNames
{
    /// <summary>
    /// The request and response Correlation ID header name.
    /// </summary>
    /// <remarks>
    /// The same name is read from the request and written to the response. A Correlation ID is a
    /// log-correlation value only; it is not unique, unguessable, or authenticated, and must never
    /// be used for authorization, idempotency, replay protection, or audit subject identity.
    /// </remarks>
    public const string CorrelationId = "x-correlation-id";
}

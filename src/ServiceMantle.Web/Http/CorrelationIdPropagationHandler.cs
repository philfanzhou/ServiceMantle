using Microsoft.AspNetCore.Http;

namespace ServiceMantle.Web.Http;

/// <summary>Propagates the current request's resolved Correlation ID to explicitly selected clients.</summary>
/// <remarks>Reads only the middleware's private slot for each send; caller outbound headers are
/// preserved. No background ID is generated. The caller owns destination trust and redirect policy;
/// the ID is neither secret nor an authentication or idempotency credential.</remarks>
public sealed class CorrelationIdPropagationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor accessor;

    /// <summary>Creates a handler that reads the current context for each request.</summary>
    public CorrelationIdPropagationHandler(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        this.accessor = accessor;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var value = accessor.HttpContext?.GetServiceMantleCorrelationId();
        if (!request.Headers.Contains(ServiceHeaderNames.CorrelationId) && CorrelationIdValue.IsAccepted(value))
            request.Headers.Add(ServiceHeaderNames.CorrelationId, value!);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                try { response?.Dispose(); }
                finally { cancellationToken.ThrowIfCancellationRequested(); }
            }
        }
    }
}

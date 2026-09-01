using System.Net;
using System.Text.Json;
using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal interface IServiceMantleLokiHttpMessageHandlerFactory
{
    HttpMessageHandler Create();
}

internal sealed class ServiceMantleLokiHttpMessageHandlerFactory : IServiceMantleLokiHttpMessageHandlerFactory
{
    public HttpMessageHandler Create() => new SocketsHttpHandler();
}

internal sealed class ServiceMantleLokiHttpMessageHandler(
    HttpMessageHandler innerHandler,
    string authorizationHeaderValue,
    ServiceMantleGrafanaLokiDeliveryCounter deliveryCounter) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("Authorization", authorizationHeaderValue);
        var eventCount = await CountEventsAsync(request.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                deliveryCounter.RecordAcknowledged(eventCount);
                return response;
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent([]),
                ReasonPhrase = null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ServiceMantleLokiDeliveryException(
                WellKnownServiceMantleGrafanaLokiErrorCodes.TransportFailed);
        }
    }

    private static async Task<int> CountEventsAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return 0;
        }

        try
        {
            var payload = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var count = 0;
            foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
            {
                count = checked(count + stream.GetProperty("values").GetArrayLength());
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }
}

internal sealed class ServiceMantleGrafanaLokiDeliveryCounter
{
    private long acceptedCount;
    private long acknowledgedCount;

    internal long NotAcknowledgedCount => Math.Max(
        0,
        Interlocked.Read(ref acceptedCount) - Interlocked.Read(ref acknowledgedCount));

    internal void RecordAccepted() => Interlocked.Increment(ref acceptedCount);

    internal void RecordAcknowledged(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref acknowledgedCount, count);
        }
    }
}

internal sealed class ServiceMantleLokiDeliveryException(string errorCode) : HttpRequestException
{
    internal string ErrorCode { get; } = errorCode;

    public override string Message => $"The Loki delivery failed ({ErrorCode}).";

    public override string ToString() =>
        $"ServiceMantleLokiDeliveryException(ErrorCode={ErrorCode})";
}

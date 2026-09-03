using System.Net.Http.Json;
using System.Text.Json;

namespace ServiceMantle.Consul;

/// <summary>Creates the default single-call HTTP adapter with redirects disabled and normal TLS verification.</summary>
public sealed class ConsulHttpClientFactory : IConsulClientFactory
{
    /// <inheritdoc />
    public IConsulClient Create(ConsulClientConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Create(configuration, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    }

    internal static IConsulClient Create(ConsulClientConfiguration configuration, HttpMessageHandler handler) =>
        new Client(configuration, new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) });

    private sealed class Client(ConsulClientConfiguration configuration, HttpClient http) : IConsulClient
    {
        public ValueTask<ConsulClientResult> RegisterAsync(ConsulServiceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            var body = JsonContent.Create(new
            {
                ID = registration.Id,
                Name = registration.Name,
                Address = registration.Address,
                Port = registration.Port,
                Check = new
                {
                    HTTP = registration.HealthUri.AbsoluteUri,
                    Interval = "10s",
                    Timeout = "2s",
                    Status = "critical"
                }
            }, options: JsonSerializerOptions.Default);
            return SendAsync("v1/agent/service/register?replace-existing-checks=true", body, cancellationToken);
        }

        public ValueTask<ConsulClientResult> DeregisterAsync(string registrationId,
            CancellationToken cancellationToken = default) =>
            SendAsync("v1/agent/service/deregister/" + Uri.EscapeDataString(registrationId), null, cancellationToken);

        private async ValueTask<ConsulClientResult> SendAsync(string path, HttpContent? content, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(configuration.Endpoint, path));
            request.Content = content;
            if (configuration.GetToken() is { } token) { request.Headers.Add("X-Consul-Token", token); }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? ConsulClientResult.Success : ConsulClientResult.Rejected;
        }
        public void Dispose() => http.Dispose();
    }
}

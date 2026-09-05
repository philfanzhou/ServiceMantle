using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ServiceMantle.Consul.Tests;

public sealed class ConsulClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_adapter_sends_one_explicit_request_with_header_only_token(bool register)
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var handler = new Handler();
        fixture.ClientFactory.CreateClient = config => ConsulHttpClientFactory.Create(config, handler);
        using (var session = fixture.Provider.CreateClient())
        {
            Assert.Empty(handler.Requests);
            var result = register ? await session!.RegisterAsync(TestContext.Current.CancellationToken)
                : await session!.DeregisterAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ConsulClientResult.Success, result);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("PUT", request.Method);
            Assert.Equal(ConsulFixture.Secret, request.Token);
            Assert.DoesNotContain(ConsulFixture.Secret, request.Uri + request.Body);
            if (register)
            {
                Assert.Equal("https://agent.example:8501/v1/agent/service/register?replace-existing-checks=true", request.Uri);
                using var json = JsonDocument.Parse(request.Body);
                Assert.Equal(session.Registration.Id, json.RootElement.GetProperty("ID").GetString());
                Assert.Equal("orders-api", json.RootElement.GetProperty("Name").GetString());
                Assert.Equal(8080, json.RootElement.GetProperty("Port").GetInt32());
                Assert.Equal("http://orders.example:8080/health/ready", json.RootElement.GetProperty("Check").GetProperty("HTTP").GetString());
                Assert.Equal("critical", json.RootElement.GetProperty("Check").GetProperty("Status").GetString());
            }
            else
            {
                Assert.Equal("https://agent.example:8501/v1/agent/service/deregister/orders%3Ahost%2Finstance%3Fone", request.Uri);
                Assert.Empty(request.Body);
            }
        }
        Assert.True(handler.Disposed);
        Assert.Single(handler.Requests); // Dispose never silently deregisters.
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task Http_rejections_ignore_remote_body_and_do_not_retry(int status)
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var content = new NeverReadContent();
        var handler = new Handler { Reply = new HttpResponseMessage((HttpStatusCode)status) { Content = content } };
        fixture.ClientFactory.CreateClient = config => ConsulHttpClientFactory.Create(config, handler);
        using var session = fixture.Provider.CreateClient();
        var result = await session!.RegisterAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConsulClientResult.Rejected, result);
        Assert.Single(handler.Requests);
        Assert.False(content.Read);
        Assert.DoesNotContain(ConsulFixture.Secret, result.ToString());
    }

    [Theory]
    [InlineData(true, "throw")]
    [InlineData(false, "throw")]
    [InlineData(true, "internal-cancel")]
    [InlineData(false, "internal-cancel")]
    [InlineData(true, "unknown-result")]
    [InlineData(false, "unknown-result")]
    public async Task Replacement_client_failures_are_safe_and_finite(bool register, string scenario)
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var stub = new ConsulFixture.StubClient
        {
            Operation = _ => scenario switch
        {
            "throw" => ValueTask.FromException<ConsulClientResult>(new HttpRequestException(ConsulFixture.Secret)),
            "internal-cancel" => ValueTask.FromException<ConsulClientResult>(new OperationCanceledException(ConsulFixture.Secret)),
            _ => ValueTask.FromResult((ConsulClientResult)999)
        }
        };
        fixture.ClientFactory.CreateClient = _ => stub;
        using var session = fixture.Provider.CreateClient();
        var result = register ? await session!.RegisterAsync(TestContext.Current.CancellationToken)
            : await session!.DeregisterAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConsulClientResult.Unavailable, result);
        Assert.Equal(1, stub.Calls);
    }

    [Theory]
    [InlineData(true, "before")]
    [InlineData(false, "before")]
    [InlineData(true, "during")]
    [InlineData(false, "during")]
    [InlineData(true, "after")]
    [InlineData(false, "after")]
    [InlineData(true, "different-exception")]
    [InlineData(false, "different-exception")]
    public async Task Caller_cancellation_has_priority_and_never_exposes_transport_exception(bool register, string point)
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        using var cancellation = new CancellationTokenSource();
        var stub = new ConsulFixture.StubClient
        {
            Operation = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return point switch
            {
                "during" => ValueTask.FromException<ConsulClientResult>(new OperationCanceledException(ConsulFixture.Secret)),
                "different-exception" => ValueTask.FromException<ConsulClientResult>(new HttpRequestException(ConsulFixture.Secret)),
                _ => ValueTask.FromResult(ConsulClientResult.Success)
            };
        }
        };
        fixture.ClientFactory.CreateClient = _ => stub;
        using var session = fixture.Provider.CreateClient();
        if (point == "before") { cancellation.Cancel(); }
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => register
            ? session!.RegisterAsync(cancellation.Token).AsTask() : session!.DeregisterAsync(cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(ConsulFixture.Secret, exception.ToString());
        Assert.Equal(point == "before" ? 0 : 1, stub.Calls);
    }

    [Fact]
    public async Task Simultaneous_sessions_keep_snapshot_credentials_and_registrations_together()
    {
        using var fixture = new ConsulFixture();
        var handler = new Handler();
        // Each session owns its handler in production. These forwarding handlers share only the recorder.
        fixture.ClientFactory.CreateClient = config => ConsulHttpClientFactory.Create(config, new Forwarder(handler));
        await fixture.ActivateAsync(ConsulFixture.Enabled("token-one"));
        using var first = fixture.Provider.CreateClient();
        var next = ConsulFixture.Enabled("token-two"); next[ConsulSettingDefinitions.ServiceName] = "other-api";
        await fixture.ActivateAsync(next, 2);
        using var second = fixture.Provider.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var session = i % 2 == 0 ? first! : second!;
            Assert.Equal(ConsulClientResult.Success, await session.RegisterAsync(TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(40, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(request.Token == "token-one" ? "orders-api" : "other-api", body.RootElement.GetProperty("Name").GetString());
        }
    }

    [Fact]
    public async Task Default_transport_does_not_follow_a_real_http_redirect_with_the_token()
    {
        using var fixture = new ConsulFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var raw = ConsulFixture.Enabled(); raw[ConsulSettingDefinitions.Endpoint] = $"http://127.0.0.1:{endpoint.Port}";
        await fixture.ActivateAsync(raw);
        fixture.ClientFactory.CreateClient = new ConsulHttpClientFactory().Create;
        using var session = fixture.Provider.CreateClient();
        var call = session!.DeregisterAsync(TestContext.Current.CancellationToken).AsTask();
        using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new List<string>();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { Length: > 0 } line) { headers.Add(line); }
        Assert.Contains(headers, h => h == "X-Consul-Token: " + ConsulFixture.Secret);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{endpoint.Port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, TestContext.Current.CancellationToken);
        // Following the redirect would prevent completion here until another connection is served.
        Assert.Equal(ConsulClientResult.Rejected, await call.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(listener.Pending());
    }

    private sealed record Captured(string Method, string Uri, string? Token, string Body);
    private sealed class Handler : HttpMessageHandler
    {
        internal ConcurrentQueue<Captured> Requests = new();
        internal HttpResponseMessage? Reply;
        internal bool Disposed;
        internal async Task<HttpResponseMessage> RecordAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Enqueue(new(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-Consul-Token", out var values) ? values.Single() : null, body));
            return Reply ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => RecordAsync(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Forwarder(Handler handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler.RecordAsync(request, token);
    }
    private sealed class NeverReadContent : HttpContent
    {
        internal bool Read;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Read = true; throw new InvalidOperationException(ConsulFixture.Secret); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

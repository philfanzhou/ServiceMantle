using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using ServiceMantle.Web.Http;
using Xunit;

namespace ServiceMantle.Web.Tests;

public sealed class CorrelationIdPropagationTests
{
    [Theory]
    [InlineData("caller-42")]
    [InlineData("bad value")]
    [InlineData("repeated")]
    public async Task Middleware_to_factory_client_propagates_only_the_resolved_slot(string incoming)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceMantle(ServiceId.Parse("propagation"), InstanceId.Parse("propagation-01"));
        string? outbound = null;
        services.AddHttpClient("trusted").AddServiceMantleCorrelationIdPropagation()
            .ConfigurePrimaryHttpMessageHandler(() => new Inner((request, _) =>
            {
                outbound = request.Headers.GetValues(ServiceHeaderNames.CorrelationId).Single();
                return Task.FromResult(new HttpResponseMessage());
            }));
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);
        builder.UseServiceMantleCorrelationId();
        builder.Run(async context =>
        {
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("trusted");
            using var response = await client.GetAsync("https://trusted.invalid", TestContext.Current.CancellationToken);
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers[ServiceHeaderNames.CorrelationId] = incoming == "repeated" ? new[] { "first", "second" } : incoming;
        await builder.Build()(context);
        Assert.Equal(context.GetServiceMantleCorrelationId(), outbound);
        Assert.True(CorrelationIdValue.IsAccepted(outbound));
        Assert.Equal(incoming == "repeated" ? "first,second" : incoming, context.Request.Headers[ServiceHeaderNames.CorrelationId].ToString());
    }

    [Fact]
    public async Task Explicit_named_registration_is_idempotent_and_pooled_handler_reads_64_concurrent_contexts()
    {
        var services = new ServiceCollection();
        var headers = new ConcurrentDictionary<string, string?>();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, _) =>
        {
            headers[request.RequestUri!.AbsolutePath] = request.Headers.TryGetValues(ServiceHeaderNames.CorrelationId, out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage());
        };
        var trusted = services.AddHttpClient("trusted").ConfigurePrimaryHttpMessageHandler(() => new Inner(send));
        trusted.AddServiceMantleCorrelationIdPropagation().AddServiceMantleCorrelationIdPropagation();
        services.AddHttpClient("unattached").ConfigurePrimaryHttpMessageHandler(() => new Inner(send));
        using var provider = services.BuildServiceProvider();
        Assert.Single(services, d => d.ServiceType == typeof(CorrelationIdPropagationHandler));
        Assert.Equal(ServiceLifetime.Transient, services.Single(d => d.ServiceType == typeof(CorrelationIdPropagationHandler)).Lifetime);
        Assert.Equal(2, provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("trusted").HttpMessageHandlerBuilderActions.Count);
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        using var client = factory.CreateClient("trusted");
        await Task.WhenAll(Enumerable.Range(0, 64).Select(async number =>
        {
            var context = new DefaultHttpContext();
            CorrelationIdRequestSlot.Set(context, "request-" + number);
            accessor.HttpContext = context;
            await Task.Yield();
            using var response = await client.GetAsync("https://trusted.invalid/" + number, TestContext.Current.CancellationToken);
            Assert.Equal("request-" + number, headers["/" + number]);
        }));
        accessor.HttpContext = null;
        using (var response = await client.GetAsync("https://trusted.invalid/no-context", TestContext.Current.CancellationToken)) Assert.Null(headers["/no-context"]);
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Request.Headers[ServiceHeaderNames.CorrelationId] = "raw-unresolved";
        using (var response = await client.GetAsync("https://trusted.invalid/no-slot", TestContext.Current.CancellationToken)) Assert.Null(headers["/no-slot"]);
        CorrelationIdRequestSlot.Set(accessor.HttpContext, "valid-slot");
        using var unattached = factory.CreateClient("unattached");
        using (var response = await unattached.GetAsync("https://trusted.invalid/unattached", TestContext.Current.CancellationToken)) Assert.Null(headers["/unattached"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad value")]
    [InlineData("bad\nsecret")]
    [InlineData("-invalid")]
    public async Task Invalid_private_slot_never_adds_a_header(string value)
    {
        var context = new DefaultHttpContext();
        CorrelationIdRequestSlot.Set(context, value);
        using var client = new HttpClient(new CorrelationIdPropagationHandler(new HttpContextAccessor { HttpContext = context })
        { InnerHandler = new Inner((request, _) => { Assert.False(request.Headers.Contains(ServiceHeaderNames.CorrelationId)); return Task.FromResult(new HttpResponseMessage()); }) });
        using var response = await client.GetAsync("https://trusted.invalid", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Caller_outbound_headers_are_preserved_including_multiple_invalid_values()
    {
        var context = new DefaultHttpContext();
        CorrelationIdRequestSlot.Set(context, "valid-slot");
        var values = new[] { "bad value", "second value" };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://trusted.invalid");
        request.Headers.TryAddWithoutValidation(ServiceHeaderNames.CorrelationId, values);
        using var client = new HttpClient(new CorrelationIdPropagationHandler(new HttpContextAccessor { HttpContext = context })
        { InnerHandler = new Inner((message, _) => { Assert.Equal(values, message.Headers.GetValues(ServiceHeaderNames.CorrelationId)); return Task.FromResult(new HttpResponseMessage()); }) });
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_response_or_failure_observes_caller_cancel_without_secret_inner_and_cleans_owned_response(bool fail)
    {
        using var caller = new CancellationTokenSource();
        var content = new DisposableContent();
        var response = new HttpResponseMessage { Content = content };
        var requestContent = new DisposableContent();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://trusted.invalid") { Content = requestContent };
        using var invoker = new HttpMessageInvoker(new CorrelationIdPropagationHandler(new HttpContextAccessor())
        { InnerHandler = new Inner((message, token) =>
            {
                Assert.Same(request, message);
                Assert.Equal(caller.Token, token);
                caller.Cancel();
                if (fail) throw new InvalidOperationException("secret-inner");
                return Task.FromResult(response);
            }) });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret-inner", error.ToString());
        Assert.Equal(!fail, content.Disposed);
        Assert.False(requestContent.Disposed);
        request.Headers.Add("x-still-owned", "true");
        response.Dispose();
    }

    [Fact]
    public async Task Entry_cancellation_never_calls_inner_and_uncancelled_completion_preserves_response_and_exception()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var calls = 0;
        using var invoker = new HttpMessageInvoker(new CorrelationIdPropagationHandler(new HttpContextAccessor())
        { InnerHandler = new Inner((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage()); }) });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://trusted.invalid");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, calls);
        var response = new HttpResponseMessage();
        using var successful = new HttpMessageInvoker(new CorrelationIdPropagationHandler(new HttpContextAccessor())
        { InnerHandler = new Inner((_, _) => Task.FromResult(response)) });
        Assert.Same(response, await successful.SendAsync(request, TestContext.Current.CancellationToken));
        response.Dispose();
        var original = new InvalidOperationException("original");
        using var failing = new HttpMessageInvoker(new CorrelationIdPropagationHandler(new HttpContextAccessor())
        { InnerHandler = new Inner((_, _) => throw original) });
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SendAsync(request, TestContext.Current.CancellationToken)));
    }

    private sealed class Inner(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class DisposableContent : StringContent
    {
        internal bool Disposed;
        internal DisposableContent() : base("payload") { }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

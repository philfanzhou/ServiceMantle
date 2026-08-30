using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleSecurityResponseHeadersTests
{
    private static readonly IReadOnlyDictionary<string, string> MandatoryHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
            ["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
        };

    [Theory]
    [InlineData("/ok", 200)]
    [InlineData("/no-content", 204)]
    [InlineData("/bad-request", 400)]
    [InlineData("/handled-error", 500)]
    public async Task Marked_endpoint_responses_receive_exact_mandatory_single_values(
        string path,
        int expectedStatus)
    {
        await using var app = await StartHostAsync();

        using var response = await app.GetTestClient().GetAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        AssertMandatoryHeaders(response);
    }

    [Fact]
    public async Task Downstream_values_with_repeated_and_different_casing_are_collapsed()
    {
        await using var app = await StartHostAsync();

        using var response = await app.GetTestClient().GetAsync(
            "/overwrite",
            TestContext.Current.CancellationToken);

        AssertMandatoryHeaders(response);
        Assert.DoesNotContain(
            response.Headers.SelectMany(header => header.Value),
            value => value.Contains("downstream", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmarked_endpoints_and_consumer_headers_are_unchanged()
    {
        await using var app = await StartHostAsync();

        using var response = await app.GetTestClient().GetAsync(
            "/unmarked",
            TestContext.Current.CancellationToken);

        Assert.Equal("consumer-cache", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("kept", Assert.Single(response.Headers.GetValues("X-Consumer-Header")));
        Assert.DoesNotContain("X-Frame-Options", response.Headers.Select(header => header.Key));
    }

    [Fact]
    public async Task Response_started_before_middleware_is_left_unchanged_without_throwing()
    {
        await using var app = await StartHostAsync();

        using var response = await app.GetTestClient().GetAsync(
            "/already-started",
            TestContext.Current.CancellationToken);

        Assert.Equal("already-sent", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.DoesNotContain("X-Frame-Options", response.Headers.Select(header => header.Key));
    }

    [Fact]
    public async Task Registration_is_idempotent_marker_is_deduplicated_and_no_options_are_exported()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0.0");
        serviceMantle.AddSecurityResponseHeaders().AddSecurityResponseHeaders();
        await using var app = builder.Build();
        app.UseServiceMantleSecurityResponseHeaders();
        app.MapGet("/duplicate", () => Results.Ok())
            .RequireServiceMantleSecurityResponseHeaders()
            .RequireServiceMantleSecurityResponseHeaders();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        Assert.Single(endpoint.Metadata.OfType<ServiceMantleSecurityResponseHeadersMetadata>());
        Assert.DoesNotContain(
            typeof(ServiceMantleSecurityResponseHeadersMetadata).Assembly.ExportedTypes,
            type => type.Name.Contains("SecurityResponseHeadersOptions", StringComparison.Ordinal));
        var registrationMethod = typeof(ServiceMantleServiceCollectionExtensions)
            .GetMethod("AddSecurityResponseHeaders", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(registrationMethod);
        Assert.Single(registrationMethod.GetParameters());
    }

    [Fact]
    public async Task Capability_is_not_added_by_default_and_use_without_registration_fails()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0.0");
        await using var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => app.UseServiceMantleSecurityResponseHeaders());
    }

    [Fact]
    public async Task Middleware_registers_one_callback_for_one_marked_request()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(
                ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0.0")
            .AddSecurityResponseHeaders();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseServiceMantleSecurityResponseHeaders();
        app.Run(_ => Task.CompletedTask);
        var feature = new CountingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(feature);
        var context = new DefaultHttpContext(features) { RequestServices = provider };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ServiceMantleSecurityResponseHeadersMetadata()),
            "marked"));

        await app.Build()(context);

        Assert.Equal(1, feature.RegisteredCallbacks);
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddServiceMantle(
                ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0.0")
            .AddSecurityResponseHeaders();
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/already-started")
            {
                context.Response.Headers["Cache-Control"] = "already-sent";
                await context.Response.StartAsync();
            }

            await next(context);
        });
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (TestEndpointException)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("handled");
            }
        });
        app.UseServiceMantleSecurityResponseHeaders();

        app.MapGet("/ok", () => Results.Text("body"))
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/no-content", () => Results.NoContent())
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/bad-request", () => Results.BadRequest())
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/handled-error", ThrowEndpointAsync)
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/overwrite", (HttpContext context) =>
            {
                foreach (var name in MandatoryHeaders.Keys)
                {
                    context.Response.Headers[name.ToLowerInvariant()] = "downstream-a";
                    context.Response.Headers.Append(name.ToUpperInvariant(), "downstream-b");
                }

                context.Response.OnStarting(() =>
                {
                    foreach (var name in MandatoryHeaders.Keys)
                    {
                        context.Response.Headers.Append(name, "downstream-on-starting");
                    }

                    return Task.CompletedTask;
                });

                return Results.Ok();
            })
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/already-started", () => Results.Empty)
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/unmarked", (HttpContext context) =>
        {
            context.Response.Headers["Cache-Control"] = "consumer-cache";
            context.Response.Headers["X-Consumer-Header"] = "kept";
            return Results.Ok();
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static void AssertMandatoryHeaders(HttpResponseMessage response)
    {
        foreach (var (name, expected) in MandatoryHeaders)
        {
            Assert.True(response.Headers.TryGetValues(name, out var values), name);
            Assert.Equal(expected, Assert.Single(values));
        }

        Assert.DoesNotContain("Strict-Transport-Security", response.Headers.Select(header => header.Key));
        Assert.DoesNotContain("Access-Control-Allow-Origin", response.Headers.Select(header => header.Key));
    }

    private static Task ThrowEndpointAsync(HttpContext context) =>
        Task.FromException(new TestEndpointException());

    private sealed class TestEndpointException : Exception;

    private sealed class CountingResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => false;
        internal int RegisteredCallbacks { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => RegisteredCallbacks++;
        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}

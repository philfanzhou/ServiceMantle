using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ServiceMantle.Web.Http;
using ServiceMantle.Web.RateLimiting;
using Xunit;

namespace ServiceMantle.Web.Tests;

public sealed class RateLimitingConsumerPolicyTests
{
    private const string FirstPolicy = "admin-login";
    private const string SecondPolicy = "public-api";

    [Fact]
    public async Task Consumer_policies_coexist_with_library_policies_without_a_global_limiter()
    {
        await using var app = await StartAsync(options =>
        {
            options.ConsumerPolicies[FirstPolicy] = FastPolicy();
        });

        // All three policies answered their endpoints: the consumer policy joined the two
        // library-owned ones without introducing a global limiter.
        Assert.Null(app.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
        Assert.Equal(200, await SendAsync(app, "/first", "10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.1"));
    }

    [Fact]
    public async Task Without_consumer_policies_the_registration_shape_is_unchanged()
    {
        await using var app = await StartAsync(options => { });

        Assert.Null(app.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
        // The library setup policy behaves exactly as before: its own bucket, unaffected.
        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.1"));
    }

    [Theory]
    [InlineData("servicemantle.custom")]
    [InlineData("ServiceMantle.setup")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void Reserved_or_malformed_names_fail_the_registration_call_itself(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0.0");

        Assert.Throws<RateLimitingConfigurationException>(() => serviceMantle.AddRateLimiting(
            options => options.ConsumerPolicies[name] = new RateLimitPolicyOptions
            {
                PermitLimit = 10
            }));
    }

    [Theory]
    [InlineData(0, "ConsumerPolicies.PermitLimit")]
    [InlineData(10_001, "ConsumerPolicies.PermitLimit")]
    public async Task Out_of_range_values_fail_when_the_host_starts(int permitLimit, string fieldName)
    {
        await using var app = Build(options =>
            options.ConsumerPolicies[FirstPolicy] = new RateLimitPolicyOptions
            {
                PermitLimit = permitLimit
            });
        var exception = await Assert.ThrowsAsync<RateLimitingConfigurationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fieldName, exception.FieldName);
    }

    [Fact]
    public async Task Equivalent_repeated_registrations_are_idempotent_and_disagreeing_ones_conflict()
    {
        static void AddPolicy(RateLimitingOptions options)
        {
            options.ConsumerPolicies[FirstPolicy] = new RateLimitPolicyOptions { PermitLimit = 4 };
        }

        await using var identical = Build(AddPolicy, AddPolicy);
        await identical.StartAsync(TestContext.Current.CancellationToken);
        await identical.StopAsync(TestContext.Current.CancellationToken);

        await using var conflicting = Build(
            AddPolicy,
            options => options.ConsumerPolicies[FirstPolicy] = new RateLimitPolicyOptions
            {
                PermitLimit = 5
            });
        var exception = await Assert.ThrowsAsync<RateLimitingConfigurationException>(
            () => conflicting.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Registration", exception.FieldName);
    }

    [Fact]
    public async Task Consumer_policies_partition_by_trusted_client_with_isolated_buckets()
    {
        await using var app = await StartAsync(options =>
        {
            options.ConsumerPolicies[FirstPolicy] = FastPolicy();
            options.ConsumerPolicies[SecondPolicy] = FastPolicy();
        });

        // Each policy has its own bucket for the same client.
        Assert.Equal(200, await SendAsync(app, "/first", "10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/first", "10.0.0.1"));
        Assert.Equal(429, await SendAsync(app, "/first", "10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/second", "10.0.0.1"));

        // The library-owned setup policy stays isolated from both.
        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.1"));

        // IPv4-mapped IPv6 lands in the same partition as the plain IPv4 address.
        Assert.Equal(429, await SendAsync(app, "/first", "::ffff:10.0.0.1"));
        // Another client has its own bucket.
        Assert.Equal(200, await SendAsync(app, "/first", "10.0.0.2"));
    }

    [Fact]
    public async Task Addressless_requests_share_one_unknown_partition_per_policy()
    {
        await using var app = await StartAsync(options =>
        {
            options.ConsumerPolicies[FirstPolicy] = FastPolicy();
            options.ConsumerPolicies[SecondPolicy] = FastPolicy();
        });

        Assert.Equal(200, await SendAsync(app, "/first", remoteIp: null));
        Assert.Equal(200, await SendAsync(app, "/first", remoteIp: null));
        Assert.Equal(429, await SendAsync(app, "/first", remoteIp: null));
        // The second policy's unknown bucket is separate.
        Assert.Equal(200, await SendAsync(app, "/second", remoteIp: null));

        // A spoofed forwarding header never moves an addressless request into another partition.
        var spoofed = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = null;
            context.Request.Path = "/first";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.5";
        }, TestContext.Current.CancellationToken);
        Assert.Equal(429, spoofed.Response.StatusCode);
    }

    [Fact]
    public async Task Rejections_match_the_library_format_and_carry_no_address_or_partition_key()
    {
        await using var app = await StartAsync(options =>
        {
            options.ConsumerPolicies[FirstPolicy] = FastPolicy();
        });

        Assert.Equal(200, await SendAsync(app, "/first", "198.51.100.7"));
        Assert.Equal(200, await SendAsync(app, "/first", "198.51.100.7"));
        var rejected = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
            context.Request.Path = "/first";
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal("application/problem+json", rejected.Response.ContentType);
        // The rejection flows through the same shared OnRejected as the library policies: any
        // present Retry-After header follows the same rounding rule, and the body carries the
        // same problem-details shape and error code.
        Assert.NotEmpty(rejected.Response.Headers[ServiceHeaderNames.CorrelationId].ToString());
        using var document = await JsonDocument.ParseAsync(
            rejected.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var body = document.RootElement.GetRawText();
        Assert.Equal(
            "rate_limit.exceeded",
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("198.51.100.7", body, StringComparison.Ordinal);
        Assert.DoesNotContain("consumer:", body, StringComparison.Ordinal);
    }

    private static RateLimitPolicyOptions FastPolicy() => new()
    {
        PermitLimit = 2,
        Window = TimeSpan.FromSeconds(10),
        SegmentsPerWindow = 2
    };

    private static WebApplication Build(params Action<RateLimitingOptions>[] registrations)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0.0");
        foreach (var registration in registrations)
        {
            serviceMantle.AddRateLimiting(registration);
        }

        return builder.Build();
    }

    private static async Task<WebApplication> StartAsync(Action<RateLimitingOptions> configure)
    {
        var app = Build(configure);
        app.UseServiceMantleCorrelationId();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/setup", () => Results.Ok())
            .RequireRateLimiting(RateLimitingDefaults.SetupPolicyName);
        app.MapGet("/first", () => Results.Ok())
            .RequireRateLimiting(FirstPolicy);
        app.MapGet("/second", () => Results.Ok())
            .RequireRateLimiting(SecondPolicy);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<int> SendAsync(
        WebApplication app,
        string path,
        string? remoteIp)
    {
        var context = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
            context.Request.Path = path;
        }, TestContext.Current.CancellationToken);
        return context.Response.StatusCode;
    }
}

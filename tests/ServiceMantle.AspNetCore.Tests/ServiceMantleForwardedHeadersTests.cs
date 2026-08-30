using System.Collections;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleForwardedHeadersTests
{
    [Fact]
    public async Task Capability_is_opt_in_and_use_without_registration_fails_during_composition()
    {
        await using var app = await StartAsync(configure: null, useMiddleware: false);
        var context = await SendAsync(app, "10.0.0.1", "203.0.113.9", "https", "api.example.com");

        Assert.Equal("10.0.0.1", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("origin.internal", context.Request.Host.Value);

        var unconfigured = Build(configure: null, useMiddleware: false);
        Assert.Throws<InvalidOperationException>(() => unconfigured.UseServiceMantleForwardedHeaders());
        await unconfigured.DisposeAsync();
    }

    [Theory]
    [InlineData("proxy", "10.0.0.1", "10.0.0.1", true)]
    [InlineData("proxy", "2001:db8::1", "2001:db8::1", true)]
    [InlineData("proxy", "10.0.0.1", "::ffff:10.0.0.1", true)]
    [InlineData("network", "10.0.0.0/30", "10.0.0.0", true)]
    [InlineData("network", "10.0.0.0/30", "10.0.0.3", true)]
    [InlineData("network", "10.0.0.0/30", "10.0.0.4", false)]
    [InlineData("network", "2001:db8::/126", "2001:db8::3", true)]
    [InlineData("network", "2001:db8::/126", "2001:db8::4", false)]
    public async Task Framework_applies_ipv4_ipv6_mapped_and_cidr_boundaries(
        string kind,
        string trusted,
        string peer,
        bool expectedApplied)
    {
        await using var app = await StartAsync(options =>
        {
            if (kind == "proxy")
            {
                options.KnownProxies = [trusted];
            }
            else
            {
                options.KnownIPNetworks = [trusted];
            }
        });

        var context = await SendAsync(app, peer, "203.0.113.9", "https");

        Assert.Equal(expectedApplied ? "203.0.113.9" : IPAddress.Parse(peer).ToString(),
            context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal(expectedApplied ? "https" : "http", context.Request.Scheme);
    }

    [Theory]
    [InlineData(2, true, "198.51.100.8", "https", "")]
    [InlineData(1, true, "10.0.0.2", "http", "198.51.100.8")]
    [InlineData(2, false, "10.0.0.9", "http", "198.51.100.8")]
    public async Task Framework_consumes_right_to_left_stops_at_unknown_and_honors_limit(
        int limit,
        bool trustMiddle,
        string expectedRemote,
        string expectedScheme,
        string remainingFor)
    {
        await using var app = await StartAsync(options =>
        {
            options.KnownProxies = trustMiddle ? ["10.0.0.1", "10.0.0.2"] : ["10.0.0.1"];
            options.ForwardLimit = limit;
        });
        var middle = trustMiddle ? "10.0.0.2" : "10.0.0.9";

        var context = await SendAsync(
            app,
            "10.0.0.1",
            $"198.51.100.8, {middle}",
            "https, http");

        Assert.Equal(expectedRemote, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal(expectedScheme, context.Request.Scheme);
        Assert.Equal(remainingFor, context.Request.Headers["X-Forwarded-For"].ToString());
    }

    [Fact]
    public async Task Symmetry_failure_applies_none_of_for_proto_or_host()
    {
        await using var app = await StartAsync(options =>
        {
            options.KnownProxies = ["10.0.0.1"];
            options.AllowedHosts = ["api.example.com"];
        });

        var context = await SendAsync(
            app,
            "10.0.0.1",
            "203.0.113.9",
            "https, http",
            "api.example.com");

        Assert.Equal("10.0.0.1", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("origin.internal", context.Request.Host.Value);
    }

    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("API.EXAMPLE.COM", "API.EXAMPLE.COM")]
    [InlineData("child.example.net", "child.example.net")]
    [InlineData("xn--bcher-kva.example", "bücher.example")]
    public async Task Allowed_hosts_use_framework_concrete_wildcard_idn_and_case_matching(
        string forwardedHost,
        string expectedHost)
    {
        await using var app = await StartAsync(options =>
        {
            options.KnownProxies = ["10.0.0.1"];
            options.AllowedHosts = ["api.example.com", "*.example.net", "bücher.example"];
        });

        var context = await SendAsync(app, "10.0.0.1", "203.0.113.9", "https", forwardedHost);

        Assert.Equal(expectedHost, context.Request.Host.Value);
        Assert.Equal("203.0.113.9", context.Connection.RemoteIpAddress!.ToString());
    }

    [Fact]
    public async Task Empty_allowed_hosts_do_not_enable_forwarded_host()
    {
        await using var app = await StartAsync(options => options.KnownProxies = ["10.0.0.1"]);

        var context = await SendAsync(
            app, "10.0.0.1", "203.0.113.9", "https", "attacker.example");

        Assert.Equal("203.0.113.9", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal("origin.internal", context.Request.Host.Value);
        Assert.Equal("attacker.example", context.Request.Headers["X-Forwarded-Host"].ToString());
    }

    [Fact]
    public async Task Disallowed_forwarded_host_causes_symmetric_batch_rejection()
    {
        await using var app = await StartAsync(options =>
        {
            options.KnownProxies = ["10.0.0.1"];
            options.AllowedHosts = ["api.example.com"];
        });

        var context = await SendAsync(
            app, "10.0.0.1", "203.0.113.9", "https", "attacker.example");

        Assert.Equal("10.0.0.1", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("origin.internal", context.Request.Host.Value);
    }

    [Fact]
    public async Task Unknown_peer_changes_nothing_even_if_shared_framework_options_trust_everyone()
    {
        await using var app = await StartAsync(
            options => options.KnownProxies = ["10.0.0.1"],
            configureServices: services => services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                options.ForwardedHeaders = ForwardedHeaders.All;
            }));

        var context = await SendAsync(
            app, "10.0.0.99", "secret-header-ip", "secret-header-proto", "secret-header-host");

        Assert.Equal("10.0.0.99", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("origin.internal", context.Request.Host.Value);
    }

    [Theory]
    [InlineData("limit-null", "ForwardLimit", "forwarded_headers.invalid_forward_limit")]
    [InlineData("limit-zero", "ForwardLimit", "forwarded_headers.invalid_forward_limit")]
    [InlineData("limit-high", "ForwardLimit", "forwarded_headers.invalid_forward_limit")]
    [InlineData("missing", "KnownProxies/KnownIPNetworks", "forwarded_headers.trusted_proxy_required")]
    [InlineData("proxy", "KnownProxies", "forwarded_headers.invalid_value")]
    [InlineData("network", "KnownIPNetworks", "forwarded_headers.invalid_value")]
    [InlineData("duplicate", "KnownProxies", "forwarded_headers.duplicate_value")]
    [InlineData("duplicate-network", "KnownIPNetworks", "forwarded_headers.duplicate_value")]
    [InlineData("duplicate-host", "AllowedHosts", "forwarded_headers.duplicate_value")]
    [InlineData("wildcard", "AllowedHosts", "forwarded_headers.invalid_value")]
    [InlineData("normalized-wildcard", "AllowedHosts", "forwarded_headers.invalid_value")]
    [InlineData("port", "AllowedHosts", "forwarded_headers.invalid_value")]
    [InlineData("empty-host", "AllowedHosts", "forwarded_headers.invalid_value")]
    [InlineData("enumeration", "KnownProxies", "forwarded_headers.enumeration_failed")]
    public async Task Invalid_configuration_fails_startup_with_safe_stable_metadata(
        string shape,
        string expectedField,
        string expectedCode)
    {
        const string sensitive = "sensitive-config-value";
        var app = Build(options => ConfigureInvalid(options, shape, sensitive), useMiddleware: false);

        var exception = await Assert.ThrowsAsync<ServiceMantleForwardedHeadersConfigurationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));
        await app.DisposeAsync();

        Assert.Equal(expectedField, exception.FieldName);
        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.DoesNotContain(sensitive, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identical_normalized_registration_is_idempotent_but_conflict_fails_startup()
    {
        await using var identical = BuildWithRegistrations(
            options =>
            {
                options.KnownProxies = ["10.0.0.1", "2001:DB8::1"];
                options.AllowedHosts = ["BÜCHER.example", "api.example.com"];
            },
            options =>
            {
                options.KnownProxies = ["2001:db8::1", "10.0.0.1"];
                options.AllowedHosts = ["API.EXAMPLE.COM", "xn--bcher-kva.example"];
            });
        await identical.StartAsync(TestContext.Current.CancellationToken);
        await identical.StopAsync(TestContext.Current.CancellationToken);

        var conflicting = BuildWithRegistrations(
            options => options.KnownProxies = ["10.0.0.1"],
            options => options.KnownProxies = ["10.0.0.2"]);
        var exception = await Assert.ThrowsAsync<ServiceMantleForwardedHeadersConfigurationException>(
            () => conflicting.StartAsync(TestContext.Current.CancellationToken));
        await conflicting.DisposeAsync();
        Assert.Equal(
            WellKnownForwardedHeadersConfigurationErrorCodes.ConflictingRegistration,
            exception.ErrorCode);
    }

    [Fact]
    public async Task Collections_are_enumerated_once_and_mutation_after_start_does_not_change_snapshot()
    {
        var proxies = new CountingEnumerable(["10.0.0.1"]);
        var hosts = new List<string> { "api.example.com" };
        await using var app = await StartAsync(options =>
        {
            options.KnownProxies = proxies;
            options.AllowedHosts = hosts;
        });
        proxies.Values.Clear();
        proxies.Values.Add("10.0.0.99");
        hosts.Clear();
        hosts.Add("attacker.example");

        var trusted = await SendAsync(app, "10.0.0.1", "203.0.113.9", "https", "api.example.com");
        var later = await SendAsync(app, "10.0.0.99", "203.0.113.9", "https", "attacker.example");

        Assert.Equal(1, proxies.EnumerationCount);
        Assert.Equal("203.0.113.9", trusted.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("api.example.com", trusted.Request.Host.Value);
        Assert.Equal("10.0.0.99", later.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("origin.internal", later.Request.Host.Value);
    }

    private static void ConfigureInvalid(
        ServiceMantleForwardedHeadersOptions options,
        string shape,
        string sensitive)
    {
        options.KnownProxies = ["10.0.0.1"];
        switch (shape)
        {
            case "limit-null": options.ForwardLimit = null; break;
            case "limit-zero": options.ForwardLimit = 0; break;
            case "limit-high": options.ForwardLimit = 11; break;
            case "missing": options.KnownProxies = []; break;
            case "proxy": options.KnownProxies = [sensitive]; break;
            case "network": options.KnownIPNetworks = [sensitive]; break;
            case "duplicate": options.KnownProxies = ["10.0.0.1", " 10.0.0.1 "]; break;
            case "duplicate-network": options.KnownIPNetworks = ["10.0.0.0/24", " 10.0.0.0/24 "]; break;
            case "duplicate-host": options.AllowedHosts = ["BÜCHER.example", "xn--bcher-kva.example"]; break;
            case "wildcard": options.AllowedHosts = ["*"]; break;
            case "normalized-wildcard": options.AllowedHosts = ["[0:0:0:0:0:0:0:0]"]; break;
            case "port": options.AllowedHosts = [$"{sensitive}:443"]; break;
            case "empty-host": options.AllowedHosts = [" "]; break;
            case "enumeration": options.KnownProxies = new ThrowingEnumerable(sensitive); break;
        }
    }

    private static WebApplication Build(
        Action<ServiceMantleForwardedHeadersOptions>? configure,
        bool useMiddleware = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0.0");
        if (configure is not null)
        {
            serviceMantle.AddForwardedHeaders(configure);
        }

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        if (useMiddleware)
        {
            app.UseServiceMantleForwardedHeaders();
        }

        app.Run(_ => Task.CompletedTask);
        return app;
    }

    private static WebApplication BuildWithRegistrations(
        params Action<ServiceMantleForwardedHeadersOptions>[] registrations)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0.0");
        foreach (var registration in registrations)
        {
            serviceMantle.AddForwardedHeaders(registration);
        }

        var app = builder.Build();
        app.Run(_ => Task.CompletedTask);
        return app;
    }

    private static async Task<WebApplication> StartAsync(
        Action<ServiceMantleForwardedHeadersOptions>? configure,
        bool useMiddleware = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var app = Build(configure, useMiddleware, configureServices);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpContext> SendAsync(
        WebApplication app,
        string peer,
        string forwardedFor,
        string forwardedProto,
        string? forwardedHost = null) =>
        app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Connection.RemotePort = 44321;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("origin.internal");
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            if (forwardedHost is not null)
            {
                context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            }
        }, TestContext.Current.CancellationToken);

    private sealed class CountingEnumerable(IEnumerable<string> values) : IEnumerable<string>
    {
        internal List<string> Values { get; } = values.ToList();
        internal int EnumerationCount { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            return Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEnumerable(string sensitive) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException(sensitive);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

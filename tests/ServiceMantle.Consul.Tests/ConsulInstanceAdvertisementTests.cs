using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using ServiceMantle.Discovery;
using Xunit;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// Covers the per-instance advertised address and port: registration-time validation with the
/// service-level rules, the override of Address, Port, and HealthUri only, the unchanged
/// service-level combination validation, repeated-registration agreement, two instances of one
/// snapshot advertising different endpoints, and the non-disclosure boundary.
/// </summary>
public sealed class ConsulInstanceAdvertisementTests
{
    private const string SentinelAddress = "instance-sentinel.example";

    [Theory]
    // Blank, whitespace, URI delimiters, and other illegal characters.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host name")]
    [InlineData("host:80")]
    [InlineData("a?b")]
    [InlineData("u@v")]
    [InlineData("a/b")]
    [InlineData("254-characters-of-dns.a23456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public void F1_An_unusable_address_fails_the_registration_call_without_descriptors(string address)
    {
        var services = new ServiceCollection();

        Assert.Throws<ConsulConfigurationException>(() =>
            services.AddServiceMantleConsul(null, options =>
            {
                options.Address = address;
                options.Port = 5001;
            }));

        Assert.Empty(services);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void F1_An_out_of_range_port_fails_the_registration_call_without_descriptors(int port)
    {
        var services = new ServiceCollection();

        Assert.Throws<ConsulConfigurationException>(() =>
            services.AddServiceMantleConsul(null, options =>
            {
                options.Address = "10.0.0.7";
                options.Port = port;
            }));

        Assert.Empty(services);
    }

    [Fact]
    public void F1_Supplying_only_one_of_the_two_values_fails_the_registration_call()
    {
        var addressOnly = new ServiceCollection();
        var portOnly = new ServiceCollection();

        Assert.Throws<ConsulConfigurationException>(() =>
            addressOnly.AddServiceMantleConsul(null, options => options.Address = "10.0.0.7"));
        Assert.Throws<ConsulConfigurationException>(() =>
            portOnly.AddServiceMantleConsul(null, options => options.Port = 5001));

        Assert.Empty(addressOnly);
        Assert.Empty(portOnly);
    }

    [Theory]
    [InlineData("10.0.0.7")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("instance-1.example")]
    [InlineData("a")]
    public void F1_Ip_literals_and_dns_names_pass(string address)
    {
        var services = new ServiceCollection();

        services.AddServiceMantleConsul(null, options =>
        {
            options.Address = address;
            options.Port = 5001;
        });

        Assert.NotEmpty(services);
    }

    [Fact]
    public async Task F2_An_instance_advertisement_overrides_address_port_and_health_uri_only()
    {
        using var fixture = new ConsulFixture(advertisement: options =>
        {
            options.Address = "10.0.0.7";
            options.Port = 5001;
        });
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var handler = new Handler();
        fixture.ClientFactory.CreateClient = config => ConsulHttpClientFactory.Create(config, handler);

        using var session = fixture.Provider.CreateClient();
        Assert.Equal("10.0.0.7", session!.Registration.Address);
        Assert.Equal(5001, session.Registration.Port);
        Assert.Equal("http://10.0.0.7:5001/health/ready", session.Registration.HealthUri.AbsoluteUri);
        Assert.Equal("orders:host/instance?one", session.Registration.Id);
        Assert.Equal("orders-api", session.Registration.Name);

        await session.RegisterAsync(TestContext.Current.CancellationToken);
        var request = Assert.Single(handler.Requests);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("10.0.0.7", json.RootElement.GetProperty("Address").GetString());
        Assert.Equal(5001, json.RootElement.GetProperty("Port").GetInt32());
        Assert.Equal(
            "http://10.0.0.7:5001/health/ready",
            json.RootElement.GetProperty("Check").GetProperty("HTTP").GetString());
        Assert.Equal("orders:host/instance?one", json.RootElement.GetProperty("ID").GetString());
        Assert.Equal("orders-api", json.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task F3_An_unconfigured_advertisement_keeps_the_service_level_values()
    {
        using var fixture = new ConsulFixture(advertisement: options => { });
        await fixture.ActivateAsync(ConsulFixture.Enabled());

        using var session = fixture.Provider.CreateClient();

        Assert.Equal("orders.example", session!.Registration.Address);
        Assert.Equal(8080, session.Registration.Port);
        Assert.Equal("http://orders.example:8080/health/ready", session.Registration.HealthUri.AbsoluteUri);
        Assert.Equal("orders:host/instance?one", session.Registration.Id);
    }

    [Fact]
    public async Task F4_Two_instances_of_one_snapshot_advertise_their_own_endpoints()
    {
        using var first = new ConsulFixture(advertisement: options =>
        {
            options.Address = "10.0.0.7";
            options.Port = 5001;
        });
        using var second = new ConsulFixture(advertisement: options =>
        {
            options.Address = "10.0.0.8";
            options.Port = 5002;
        }, configureServices: services =>
            services.AddSingleton(InstanceId.Parse("host/instance?two")));
        await first.ActivateAsync(ConsulFixture.Enabled());
        await second.ActivateAsync(ConsulFixture.Enabled());

        using var firstSession = first.Provider.CreateClient();
        using var secondSession = second.Provider.CreateClient();

        Assert.NotEqual(firstSession!.Registration.Id, secondSession!.Registration.Id);
        Assert.NotEqual(firstSession.Registration.Address, secondSession.Registration.Address);
        Assert.NotEqual(firstSession.Registration.Port, secondSession.Registration.Port);
        Assert.NotEqual(firstSession.Registration.HealthUri, secondSession.Registration.HealthUri);
        Assert.Equal("orders:host/instance?one", firstSession.Registration.Id);
        Assert.Equal("orders:host/instance?two", secondSession.Registration.Id);
        Assert.Equal("http://10.0.0.8:5002/health/ready", secondSession.Registration.HealthUri.AbsoluteUri);
    }

    [Fact]
    public async Task F5_Repeated_agreeing_regulations_stay_idempotent()
    {
        using var fixture = new ConsulFixture(advertisement: options =>
        {
            options.Address = "10.0.0.7";
            options.Port = 5001;
        });
        await fixture.ActivateAsync(ConsulFixture.Enabled());

        using var session = fixture.Provider.CreateClient();

        Assert.Equal("10.0.0.7", session!.Registration.Address);
        Assert.Equal(1, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public void F5_Disagreeing_advertisements_fail_at_the_first_lifecycle_resolution()
    {
        var services = BaseServices();
        services.AddServiceMantleConsul(null, options =>
        {
            options.Address = "10.0.0.7";
            options.Port = 5001;
        });
        services.AddServiceMantleConsul(null, options =>
        {
            options.Address = "10.0.0.8";
            options.Port = 5002;
        });
        var resolutions = 0;
        services.AddSingleton<IConsulClientFactory>(_ =>
        {
            resolutions++;
            return new StubFactory();
        });
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService(typeof(ConsulRegistrationLifecycle)));
        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, failure.Error);
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public void F5_One_configured_and_one_unconfigured_advertisement_is_a_conflict()
    {
        var services = BaseServices();
        services.AddServiceMantleConsul(null, options =>
        {
            options.Address = "10.0.0.7";
            options.Port = 5001;
        });
        services.AddServiceMantleConsul(null, options => { });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService(typeof(ConsulRegistrationLifecycle)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F6_The_service_level_combination_still_requires_service_level_values(bool advertised)
    {
        using var fixture = new ConsulFixture(
            composite: false,
            advertisement: advertised
                ? options =>
                {
                    options.Address = "10.0.0.7";
                    options.Port = 5001;
                }
                : null);
        var raw = ConsulFixture.Enabled();
        raw.Remove(ConsulSettingDefinitions.Address);
        await fixture.ActivateAsync(raw);

        // The instance-level advertisement never relaxes the enabled snapshot's own requirement.
        Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient());
    }

    [Fact]
    public async Task F7_The_instance_address_never_reaches_a_message_or_a_toString()
    {
        using var fixture = new ConsulFixture(advertisement: options =>
        {
            options.Address = SentinelAddress;
            options.Port = 5001;
        });
        await fixture.ActivateAsync(ConsulFixture.Enabled());

        using var session = fixture.Provider.CreateClient();
        Assert.DoesNotContain(SentinelAddress, session!.ToString(), StringComparison.Ordinal);
        Assert.Equal("ConsulServiceRegistration", session.Registration.ToString());
        // The health URI is the one place the address is transport data by contract; every other
        // projection stays metadata only.
        Assert.StartsWith(
            "http://" + SentinelAddress + ":",
            session.Registration.HealthUri.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void F7_A_disagreeing_registration_message_names_no_address()
    {
        var services = BaseServices();
        services.AddServiceMantleConsul(null, options =>
        {
            options.Address = SentinelAddress;
            options.Port = 5001;
        });
        services.AddServiceMantleConsul(null, options => { });
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService(typeof(ConsulRegistrationLifecycle)));
        Assert.DoesNotContain(SentinelAddress, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelAddress, failure.ToString(), StringComparison.Ordinal);
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(ConsulFixture.Service);
        services.AddSingleton(ConsulFixture.Instance);
        services.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(new ServiceSettingCurrentSnapshotAccessor());
        return services;
    }

    private sealed class StubFactory : IConsulClientFactory
    {
        public IConsulClient Create(ConsulClientConfiguration configuration) =>
            throw new InvalidOperationException("never resolved in these tests");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<(string Method, string Uri, string? Token, string Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-Consul-Token", out var values) ? values.Single() : null,
                body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

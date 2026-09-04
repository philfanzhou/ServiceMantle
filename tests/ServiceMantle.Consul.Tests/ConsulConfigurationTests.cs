using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using Xunit;
using static ServiceMantle.Consul.ConsulSettingDefinitions;

namespace ServiceMantle.Consul.Tests;

public sealed class ConsulConfigurationTests
{
    [Fact]
    public async Task Disabled_default_ignores_enabled_only_values_without_resolving_the_factory()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(new() { [Endpoint] = "invalid-secret", [Token] = "bad\r\ntoken", [Port] = "-1" });
        Assert.Null(fixture.Provider.CreateClient());
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
        Assert.Equal(0, fixture.ClientFactory.Calls);
        Assert.Single(fixture.Services.GetServices<IServiceSettingDefinitionProvider>());
        Assert.Single(fixture.Services.GetServices<IServiceSettingCompositeValidator>());
    }

    [Fact]
    public void Unavailable_snapshot_fails_without_creating_a_client()
    {
        using var fixture = new ConsulFixture();
        var exception = Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient());
        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable, exception.Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Enabled_configuration_uses_validated_snapshot_and_explicit_secret_access_only()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled(), 3);
        using var client = fixture.Provider.CreateClient();
        Assert.NotNull(client);
        Assert.Equal(3, client.SnapshotVersion);
        Assert.Equal("orders:host/instance?one", client.Registration.Id);
        Assert.Equal("orders-api", client.Registration.Name);
        Assert.Equal("orders.example", client.Registration.Address);
        Assert.Equal(8080, client.Registration.Port);
        Assert.Equal("http://orders.example:8080/health/ready", client.Registration.HealthUri.AbsoluteUri);
        var configuration = fixture.ClientFactory.Configuration!;
        Assert.Equal(ConsulFixture.Secret, configuration.GetToken());
        Assert.True(configuration.HasToken);
        Assert.Equal(1, fixture.ClientFactory.Resolutions);
        Assert.Equal(1, fixture.ClientFactory.Calls);
        foreach (var output in new[] { JsonSerializer.Serialize(configuration), configuration.ToString(),
            JsonSerializer.Serialize(client), client.ToString(), JsonSerializer.Serialize(client.Registration),
            client.Registration.ToString(), JsonSerializer.Serialize(fixture.Registry.Validate(ConsulFixture.Enabled())) })
        { Assert.DoesNotContain(ConsulFixture.Secret, output); }
        var query = new ServiceSettingQueryService(fixture.Registry, fixture.Loader);
        Assert.DoesNotContain(ConsulFixture.Secret, JsonSerializer.Serialize(await query.GetCurrentAsync(TestContext.Current.CancellationToken)));
    }

    public static TheoryData<string, string?> InvalidValues => new()
    {
        { Endpoint, null }, { Endpoint, "https://user:secret@agent.example" },
        { Endpoint, "http://agent.example:8500" }, { Endpoint, "https://agent.example?token=secret" },
        { Endpoint, "https://agent.example/#secret" }, { Endpoint, "https://agent.example/path" },
        { Endpoint, "file:///secret" }, { Endpoint, " https://agent.example" }, { Endpoint, "\0https://agent.example" },
        { Token, "" }, { Token, "secret\r\nHeader: value" }, { Token, "has space" }, { Token, "非ASCII" },
        { Token, new string('a', 4097) },
        { ServiceName, null }, { ServiceName, "" }, { ServiceName, "bad_name" },
        { ServiceName, "-bad" }, { ServiceName, "bad-" }, { ServiceName, new string('a',64) },
        { Address, null }, { Address, "https://host" }, { Address, "host/path" }, { Address, "host?secret" },
        { Port, null }, { Port, "0" }, { Port, "65536" }, { Port, "1.1" },
        { HealthPath, "//other-host/path" }, { HealthPath, "relative" }, { HealthPath, "/../secret" },
        { HealthPath, "/health?token=secret" }, { HealthPath, "/health#secret" }, { HealthPath, "/%2fsecret" },
        { HealthScheme, "ftp" }, { HealthScheme, "HTTPS" }
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task Invalid_enabled_values_fail_before_activation_and_again_at_consumer_boundary(string key, string? value)
    {
        var raw = ConsulFixture.Enabled();
        if (value is null) { raw.Remove(key); } else { raw[key] = value; }
        using var validated = new ConsulFixture();
        var result = validated.Registry.Validate(raw);
        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.DoesNotContain(ConsulFixture.Secret, JsonSerializer.Serialize(result));

        using var bypassed = new ConsulFixture(composite: false);
        await bypassed.ActivateAsync(raw);
        var exception = Assert.Throws<ConsulConfigurationException>(() => bypassed.Provider.CreateClient());
        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(ConsulFixture.Secret, exception.ToString());
        Assert.Equal(0, bypassed.ClientFactory.Resolutions);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8500", "127.0.0.1")]
    [InlineData("http://[::1]:8500", "::1")]
    [InlineData("https://agent.example", "api.example")]
    public async Task Supported_hosts_and_optional_token_are_accepted(string endpoint, string address)
    {
        using var fixture = new ConsulFixture();
        var raw = ConsulFixture.Enabled(); raw[Endpoint] = endpoint; raw[Address] = address; raw.Remove(Token);
        await fixture.ActivateAsync(raw);
        using var session = fixture.Provider.CreateClient();
        Assert.NotNull(session);
        Assert.False(fixture.ClientFactory.Configuration!.HasToken);
        Assert.Null(fixture.ClientFactory.Configuration.GetToken());
    }

    [Fact]
    public async Task A_token_definition_that_loses_sensitivity_is_rejected()
    {
        using var fixture = new ConsulFixture(composite: false, tokenSensitive: false);
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var exception = Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient());
        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, exception.Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task A_snapshot_for_another_service_is_rejected_even_when_disabled()
    {
        var other = ServiceId.Parse("other");
        using var fixture = new ConsulFixture(snapshotService: other) { SnapshotService = other };
        await fixture.ActivateAsync(new());
        Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
            Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient()).Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task Factory_failures_have_no_raw_exception_or_secret()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        fixture.ClientFactory.CreateClient = _ => throw new InvalidOperationException(ConsulFixture.Secret);
        var exception = Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient());
        Assert.Equal(ConsulConfigurationError.ClientCreationFailed, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(ConsulFixture.Secret, exception.ToString());
    }

    [Fact]
    public async Task Refresh_does_not_mutate_existing_clients_and_disabled_refresh_stops_new_creation()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled("first"));
        using var first = fixture.Provider.CreateClient();
        var firstConfig = fixture.ClientFactory.Configuration!;
        var secondRaw = ConsulFixture.Enabled("second"); secondRaw[Port] = "9000";
        await fixture.ActivateAsync(secondRaw, 2);
        using var second = fixture.Provider.CreateClient();
        Assert.Equal(1, first!.SnapshotVersion); Assert.Equal(8080, first.Registration.Port);
        Assert.Equal("first", firstConfig.GetToken());
        Assert.Equal(2, second!.SnapshotVersion); Assert.Equal(9000, second.Registration.Port);
        Assert.Equal("second", fixture.ClientFactory.Configuration!.GetToken());
        await fixture.ActivateAsync(new(), 3);
        Assert.Null(fixture.Provider.CreateClient());
        Assert.Equal(2, fixture.ClientFactory.Calls);
    }

    [Fact]
    public async Task Plaintext_persisted_tokens_cannot_activate_or_reach_the_factory()
    {
        using var fixture = new ConsulFixture();
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 1,
            [new PersistedServiceSettingValue(Token, 1, ServiceSettingValueType.String, ConsulFixture.Secret)]);
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.SensitiveEnvelopeRequired, Assert.Single(result.Errors).ErrorCode);
        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable,
            Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient()).Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task Instance_identity_is_distinct_without_replacing_the_shared_service_name()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        using var first = fixture.Provider.CreateClient();
        var other = new ConsulClientProvider(fixture.Accessor, ConsulFixture.Service,
            InstanceId.Parse("host/instance?two"), () => fixture.ClientFactory);
        using var second = other.CreateClient();
        Assert.NotEqual(first!.Registration.Id, second!.Registration.Id);
        Assert.Equal(first.Registration.Name, second.Registration.Name);
    }

    [Fact]
    public async Task Malformed_utf16_instance_ids_cannot_collapse_to_one_wire_registration()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        foreach (var value in new[] { "\ud800", "\udfff", "x\ud800y" })
        {
            var provider = new ConsulClientProvider(fixture.Accessor, ConsulFixture.Service,
                InstanceId.Parse(value), () => fixture.ClientFactory);
            Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
                Assert.Throws<ConsulConfigurationException>(() => provider.CreateClient()).Error);
        }
        Assert.Equal(0, fixture.ClientFactory.Calls);
        var valid = new ConsulClientProvider(fixture.Accessor, ConsulFixture.Service,
            InstanceId.Parse("服务器😀"), () => fixture.ClientFactory);
        using var client = valid.CreateClient();
        Assert.Equal("orders:服务器😀", client!.Registration.Id);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(client.Registration));
        Assert.Equal(client.Registration.Id, json.RootElement.GetProperty("Id").GetString());
    }

    [Fact]
    public void Optional_package_does_not_reverse_core_dependency_direction()
    {
        var core = typeof(ServiceId).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(core, reference => reference.Name!.Contains("Consul", StringComparison.OrdinalIgnoreCase));
        var optional = typeof(ConsulClientProvider).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(optional, reference => reference.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(optional, reference => reference.Name == "Consul");
    }
}

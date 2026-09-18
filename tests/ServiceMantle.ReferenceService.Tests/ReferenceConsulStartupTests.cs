using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Discovery;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the configuration boundary of the sample's optional Consul wiring and its gate-off
/// path: the switches fail before any registration with messages naming only the settings, and a
/// host that never switched the registration on holds no Consul type and no discovery key.
/// </summary>
public sealed class ReferenceConsulStartupTests
{
    [Fact]
    public void D2_ConsulWithoutThePostgreSQLGateFailsBeforeAnyRegistration()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ReferenceApplication.CreateBuilder(
            [
                "--environment", "Production",
                "--" + ReferenceConsulDefaults.EnabledKey, "true",
            ]));

        Assert.Contains(ReferenceConsulDefaults.EnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains("ReferenceService:PostgreSqlStartup:Enabled", failure.Message, StringComparison.Ordinal);
    }

    // --- G3: the advertisement pair must be whole and numeric, failing with keys only ----------

    [Theory]
    [InlineData("127.0.0.1", null)]
    [InlineData(null, "5101")]
    [InlineData("127.0.0.1", "abc")]
    [InlineData("127.0.0.1", " 5101")]
    [InlineData("127.0.0.1", "+5101")]
    public void G3_AnUnusableAdvertisementPairFailsBeforeAnyRegistration(string? address, string? port)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => CreateBuilderWithGateAndConsul(address, port));

        Assert.Contains(ReferenceConsulDefaults.AdvertisedAddressKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(ReferenceConsulDefaults.AdvertisedPortKey, failure.Message, StringComparison.Ordinal);
        if (address is not null)
        {
            Assert.DoesNotContain(address, failure.Message, StringComparison.Ordinal);
        }

        if (port is not null)
        {
            Assert.DoesNotContain(port.Trim(), failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void G3_AWholeButOutOfRangePortFailsInTheSharedLayer()
    {
        // The pair parses as a whole number, so the sample hands it to the shared registration
        // entry, which owns the range rules and fails the startup itself.
        var failure = Assert.Throws<ConsulConfigurationException>(() =>
            CreateBuilderWithGateAndConsul("127.0.0.1", "0"));

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, failure.Error);
    }

    // --- G4: a switch-off host never reads the advertisement keys -------------------------------

    [Fact]
    public async Task G4_WithTheSwitchOffTheAdvertisementKeysAreNotRead()
    {
        // The half-supplied pair would fail G3 if it were read; with the switch off the builder
        // composes exactly the switch-off host.
        var builder = ReferenceApplication.CreateBuilder(
        [
            "--environment", "Production",
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey,
            "Host=127.0.0.1;Port=5432;Database=reference;Username=postgres;Password=postgres",
            "--ReferenceService:Management:RootKey", "synthetic-startup-root-key-0123456789abcdef",
            "--ReferenceService:Management:Operators:0:Id", "ops-admin",
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential", "synthetic-startup-admin-secret",
            "--" + ReferenceConsulDefaults.AdvertisedAddressKey, "127.0.0.1",
        ]);
        builder.WebHost.UseTestServer();
        await using var app = ReferenceApplication.Build(builder);

        Assert.Null(app.Services.GetService<ConsulClientProvider>());
        Assert.Null(app.Services.GetService<IConsulClientFactory>());
    }

    private static WebApplicationBuilder CreateBuilderWithGateAndConsul(string? address, string? port)
    {
        var arguments = new List<string>
        {
            "--environment", "Production",
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey,
            "Host=127.0.0.1;Port=5432;Database=reference;Username=postgres;Password=postgres",
            "--ReferenceService:Management:RootKey", "synthetic-startup-root-key-0123456789abcdef",
            "--ReferenceService:Management:Operators:0:Id", "ops-admin",
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential", "synthetic-startup-admin-secret",
            "--" + ReferenceConsulDefaults.EnabledKey, "true",
        };
        if (address is not null)
        {
            arguments.Add("--" + ReferenceConsulDefaults.AdvertisedAddressKey);
            arguments.Add(address);
        }

        if (port is not null)
        {
            arguments.Add("--" + ReferenceConsulDefaults.AdvertisedPortKey);
            arguments.Add(port);
        }

        return ReferenceApplication.CreateBuilder([.. arguments]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\u0007")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345")]
    public void D2_AnUnusableInstanceIdFailsBeforeAnyRegistration(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ReferenceApplication.CreateBuilder(
            [
                "--environment", "Production",
                "--" + ReferenceConsulDefaults.InstanceIdKey, value,
            ]));

        Assert.Contains(ReferenceConsulDefaults.InstanceIdKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D1_WithoutTheSwitchTheHostHoldsNoConsulTypeAndNoDiscoveryKey()
    {
        var builder = ReferenceApplication.CreateBuilder(["--environment", "Production"]);
        builder.WebHost.UseTestServer();
        var app = ReferenceApplication.Build(builder);
        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                app.Services.GetServices<IHostedService>(),
                service => service.GetType().Name == "ConsulRegistrationLifecycle");
            Assert.Null(app.Services.GetService<ConsulClientProvider>());
            Assert.Null(app.Services.GetService<IConsulClientFactory>());
            var keys = app.Services.GetServices<IServiceSettingDefinitionProvider>()
                .SelectMany(provider => provider.GetDefinitions())
                .Select(definition => definition.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(
            [
                "workspace.display_name",
                "workspace.integration_token",
                "workspace.item_limit",
            ],
                keys);
        }
    }
}

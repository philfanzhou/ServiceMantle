using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
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

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the sample's opt-in PostgreSQL startup deployment gate at its inputs: it is off unless
/// it is switched on, it refuses an unusable input before any side effect, it never runs together
/// with the SQLite gate, and its registration wires the target connection into the runtime
/// components.
/// </summary>
public sealed class ReferencePostgreSqlStartupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string UsableTarget =
        "Host=127.0.0.1;Port=9;Database=reference_unit;Username=reference_unit;Password=reference-unit-secret";

    [Fact]
    public async Task The_gate_is_off_by_default_and_the_skeleton_startup_is_unchanged()
    {
        using var directory = TemporaryDirectory.Create();

        var builder = ReferenceApplication.CreateBuilder(
            ["--ReferenceService:DatabasePath", directory.DatabasePath]);
        builder.WebHost.UseTestServer();
        await using var app = ReferenceApplication.Build(builder);
        await app.StartAsync(Token);

        using var client = app.GetTestClient();
        using var root = await client.GetAsync("/", Token);

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("skeleton", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Null(app.Services.GetService<ReferencePostgreSqlStartupCoordinator>());
        Assert.Null(app.Services.GetService<ReferencePostgreSqlStartupOptions>());
        Assert.Null(app.Services.GetService<ReferencePostgreSqlStartupHostedService>());
        await app.StopAsync(Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Enabling_both_gates_is_refused_before_any_registration_or_side_effect()
    {
        using var directory = TemporaryDirectory.Create();

        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--ReferenceService:DatabasePath", directory.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
            "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, UsableTarget,
        ]));

        // The message names the two switches and neither of the configured values.
        Assert.Contains(ReferenceSqliteStartupOptions.EnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(ReferencePostgreSqlStartupOptions.EnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UsableTarget, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.DatabasePath, failure.Message, StringComparison.Ordinal);
        // Nothing was created for either gate.
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DefinitelyNotAKeyword=1")]
    public void An_unusable_target_connection_is_refused_before_any_side_effect(string? connectionString)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        };
        if (connectionString is not null)
        {
            arguments.AddRange(["--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, connectionString]);
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => ReferenceApplication.CreateBuilder([.. arguments]));

        Assert.Contains(
            ReferencePostgreSqlStartupOptions.ConnectionStringKey,
            failure.Message,
            StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.DoesNotContain(connectionString, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unusable_preparation_switch_is_refused_before_any_side_effect()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, UsableTarget,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "perhaps",
        ]));

        Assert.Contains(
            ReferencePostgreSqlStartupOptions.PrepareIfMissingKey,
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("perhaps", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DefinitelyNotAKeyword=1")]
    public void An_authorized_gate_without_a_usable_administrative_connection_is_refused(string? administrative)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, UsableTarget,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "true",
        };
        if (administrative is not null)
        {
            arguments.AddRange(
                ["--" + ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey, administrative]);
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => ReferenceApplication.CreateBuilder([.. arguments]));

        Assert.Contains(
            ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey,
            failure.Message,
            StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(administrative))
        {
            Assert.DoesNotContain(administrative, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_administrative_connection_is_never_read_when_preparation_was_not_permitted()
    {
        var options = ReadOptions(
            prepareIfMissing: false,
            administrative: "DefinitelyNotAKeyword=1");

        Assert.NotNull(options);
        Assert.Null(options.AdministrativeConnectionString);

        // The composition root accepts the same inputs: an unusable administrative value is not an
        // input failure when preparation was not permitted.
        var builder = ReferenceApplication.CreateBuilder(
        [
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, UsableTarget,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--" + ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey, "DefinitelyNotAKeyword=1",
        ]);
        Assert.NotNull(builder.Services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes")]
    public void A_gate_that_is_not_explicitly_true_stays_off(string? enabled)
    {
        var configuration = new ConfigurationBuilder();
        if (enabled is not null)
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReferencePostgreSqlStartupOptions.EnabledKey] = enabled,
            });
        }

        Assert.Null(ReferencePostgreSqlStartupOptions.Read(configuration.Build()));
    }

    [Fact]
    public async Task The_registration_wires_the_target_connection_into_the_runtime_components()
    {
        var options = ReadOptions(prepareIfMissing: false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ServiceId.Parse("reference-service"));
        services.AddReferencePostgreSqlStartup(options);
        await using var container = services.BuildServiceProvider();

        using var scope = container.CreateAsyncScope();
        // The runtime context sees exactly the target connection; the administrative connection is
        // never registered anywhere.
        var dbContextOptions = scope.ServiceProvider.GetRequiredService<
            DbContextOptions<ReferencePostgreSqlDbContext>>();
        var extension = Assert.Single(
            dbContextOptions.Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal(options.TargetConnectionString, extension.ConnectionString);
        Assert.IsType<ReferencePostgreSqlInstallationInitializationExecutor>(
            scope.ServiceProvider.GetRequiredService<IDatabaseMigrationExecutor>());
        Assert.NotNull(container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>());
        Assert.NotNull(container.GetRequiredService<ReferencePostgreSqlStartupHostedService>());
    }

    internal static ReferencePostgreSqlStartupOptions ReadOptions(
        bool prepareIfMissing,
        string? administrative = null)
    {
        administrative ??= prepareIfMissing
            ? "Host=127.0.0.1;Port=9;Database=postgres;Username=reference_unit;Password=reference-unit-admin-secret"
            : null;
        var values = new Dictionary<string, string?>
        {
            [ReferencePostgreSqlStartupOptions.EnabledKey] = "true",
            [ReferencePostgreSqlStartupOptions.ConnectionStringKey] = UsableTarget,
            [ReferencePostgreSqlStartupOptions.PrepareIfMissingKey] =
                prepareIfMissing ? "true" : "false",
        };
        if (administrative is not null)
        {
            values[ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey] = administrative;
        }

        return ReferencePostgreSqlStartupOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build())!;
    }
}

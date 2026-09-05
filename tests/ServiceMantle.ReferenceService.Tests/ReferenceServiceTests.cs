using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Health;
using ServiceMantle.ReferenceService.Installation;
using ServiceMantle.ReferenceService.Management;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

public sealed class ReferenceServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Minimal_host_starts_without_creating_database_or_administrator_or_management_routes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "reference.db");
        try
        {
            var builder = ReferenceApplication.CreateBuilder(["--ReferenceService:DatabasePath", databasePath]);
            builder.WebHost.UseTestServer();
            await using var app = ReferenceApplication.Build(builder);
            await app.StartAsync(Token);
            using var client = app.GetTestClient();
            using var root = await client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("skeleton", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            foreach (var path in new[] { "/management", "/setup", "/health", "/metrics" })
            {
                using var missing = await client.GetAsync(path, Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            await using var scope = app.Services.CreateAsyncScope();
            var provider = Assert.IsType<ExternalManagementIdentityPlaceholder>(scope.ServiceProvider.GetRequiredService<IManagementIdentityProvider>());
            var identity = await ManagementIdentityProviderInvoker.InvokeAsync(provider, Token);
            Assert.Equal(ManagementIdentityStatus.Failed, identity.Status);
            Assert.Equal("reference.external_identity_not_configured", identity.ErrorCode);
            Assert.Null(identity.Identity);
            Assert.IsType<ReferenceSetupContributor>(Assert.Single(scope.ServiceProvider.GetServices<IServiceSetupContributor>()));
            Assert.IsType<ReferenceReadinessContributor>(Assert.Single(scope.ServiceProvider.GetServices<IServiceReadinessContributor>()));
            var context = scope.ServiceProvider.GetRequiredService<ReferenceDbContext>();
            Assert.Equal(typeof(ReferenceWorkspace), Assert.Single(context.Model.GetEntityTypes()).ClrType);
            Assert.Empty(context.ChangeTracker.Entries());
            await app.StopAsync(Token);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Consumer_migration_and_explicit_save_own_the_database_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Token);
        var options = new DbContextOptionsBuilder<ReferenceDbContext>().UseSqlite(connection).Options;
        await using var context = new ReferenceDbContext(options);
        Assert.Equal("20260903000000_InitialReferenceWorkspace", Assert.Single(context.Database.GetMigrations()));
        Assert.False(context.Database.HasPendingModelChanges());
        await context.Database.MigrateAsync(Token);
        var contributor = new ReferenceSetupContributor(context);
        Assert.True((await contributor.ValidateAsync(Token)).Succeeded);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.True((await contributor.RegisterAsync(Token)).Succeeded);
        Assert.Equal(EntityState.Added, Assert.Single(context.ChangeTracker.Entries()).State);
        Assert.Equal(0, await context.Workspaces.AsNoTracking().CountAsync(Token));
        await context.SaveChangesAsync(Token);
        Assert.Equal(ReferenceSettingDefinitions.DefaultDisplayName,
            (await context.Workspaces.AsNoTracking().SingleAsync(Token)).DisplayName);
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        Assert.True((await contributor.RegisterAsync(Token)).Succeeded);
        await context.SaveChangesAsync(Token);
        await transaction.RollbackAsync(Token);
        await using var observer = new ReferenceDbContext(options);
        Assert.Equal(1, await observer.Workspaces.CountAsync(Token));
    }

    [Fact]
    public void Consumer_definitions_validate_defaults_and_invalid_values_without_persistence()
    {
        var registry = new ServiceSettingDefinitionRegistry([new ReferenceSettingDefinitions()]);
        Assert.True(registry.Validate(new Dictionary<string, string?>()).IsValid);
        var invalid = registry.Validate(new Dictionary<string, string?>
        {
            ["workspace.display_name"] = "",
            ["workspace.item_limit"] = "1001"
        });
        Assert.False(invalid.IsValid);
        Assert.Equal(2, invalid.Errors.Count);
    }

    [Fact]
    public async Task Placeholder_health_never_claims_ready_and_cancellation_does_not_stage_business_work()
    {
        var snapshot = new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
        var health = new ReferenceReadinessContributor();
        var result = await health.EvaluateAsync(snapshot, Token);
        Assert.False(result.IsReady);
        Assert.Equal("reference.health_not_integrated", result.ErrorCode);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        await using var context = new ReferenceDbContext(new DbContextOptionsBuilder<ReferenceDbContext>().UseSqlite("Data Source=:memory:").Options);
        var setup = new ReferenceSetupContributor(context);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.ValidateAsync(cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.RegisterAsync(cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.EvaluateAsync(snapshot, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExternalManagementIdentityPlaceholder().GetIdentityAsync(cancellation.Token).AsTask());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Sample_references_only_public_package_projects_and_owns_its_types()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ServiceMantle.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var project = XDocument.Load(Path.Combine(root.FullName, "samples/ServiceMantle.ReferenceService/ServiceMantle.ReferenceService.csproj"));
        Assert.Equal(new[] { "../../src/ServiceMantle/ServiceMantle.csproj", "../../src/ServiceMantle.AspNetCore/ServiceMantle.AspNetCore.csproj" },
            project.Descendants("ProjectReference").Select(reference => (string)reference.Attribute("Include")!));
        Assert.DoesNotContain(project.Descendants("Compile"), item => item.Attribute("Include") is not null);
        var assembly = typeof(ReferenceApplication).Assembly;
        Assert.Empty(assembly.GetCustomAttributes<InternalsVisibleToAttribute>());
        Assert.DoesNotContain(assembly.GetTypes(), type => type.FullName?.Contains("SignaCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name?.Contains("SignaCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(new[] { typeof(ReferenceDbContext), typeof(ReferenceSettingDefinitions), typeof(ReferenceSetupContributor),
            typeof(ReferenceReadinessContributor), typeof(ExternalManagementIdentityPlaceholder) }, type => Assert.Same(assembly, type.Assembly));
    }
}

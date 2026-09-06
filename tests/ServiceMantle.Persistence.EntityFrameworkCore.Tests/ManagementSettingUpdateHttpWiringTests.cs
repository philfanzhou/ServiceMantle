using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class ManagementSettingUpdateHttpWiringTests
{
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Consumer_executor_commits_before_200_and_persists_normalized_values_and_key_only_audits()
    {
        var barrier = new CommitBarrier();
        await using var host = await EfUpdateHost.StartAsync(barrier, CommitMode.Commit);
        var cookie = await host.SignInAsync();
        barrier.Arm();
        const string body =
            "{\"expectedVersion\":0,\"changes\":[" +
            "{\"key\":\"name\",\"value\":\"Orders\"}," +
            "{\"key\":\"count\",\"value\":\"2.500\"}," +
            "{\"key\":\"enabled\",\"value\":\"TRUE\"}," +
            "{\"key\":\"payload\",\"value\":\"{ \\\"mode\\\" : true }\"}]}";
        var pending = host.PostAsync(body, cookie);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.False(pending.IsCompleted);
        barrier.Release();
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"version\":1}", await response.Content.ReadAsStringAsync(Token));

        await using var observer = host.Context();
        var setting = await observer.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(1, setting.Version);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(setting.ValuesJson)!;
        Assert.Equal("Orders", values["name"]);
        Assert.Equal("2.5", values["count"]);
        Assert.Equal("true", values["enabled"]);
        Assert.Equal("{\"mode\":true}", values["payload"]);
        var audits = await observer.Set<ManagementAuditLogEntity>().AsNoTracking().ToListAsync(Token);
        Assert.Equal(4, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.Equal("admin", audit.OperatorId);
            Assert.DoesNotContain("Orders", audit.MetadataJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("2.5", audit.MetadataJson!, StringComparison.Ordinal);
            Assert.Equal(["key"], JsonSerializer.Deserialize<Dictionary<string, string>>(audit.MetadataJson!)!.Keys);
        });

        using var remove = await host.PostAsync(
            "{\"expectedVersion\":1,\"changes\":[{\"key\":\"name\",\"value\":null}]}",
            cookie);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        observer.ChangeTracker.Clear();
        var afterRemove = await observer.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(2, afterRemove.Version);
        Assert.DoesNotContain("name", afterRemove.ValuesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_conflict_protection_and_consumer_rollback_have_no_partial_state()
    {
        await using var host = await EfUpdateHost.StartAsync(new CommitBarrier(), CommitMode.Commit);
        var cookie = await host.SignInAsync();

        using var invalid = await host.PostAsync(
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"count\",\"value\":\"not-number\"}]}",
            cookie);
        using var protectedFailure = await host.PostAsync(
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"secret\",\"value\":\"plaintext\"}]}",
            cookie);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, protectedFailure.StatusCode);

        using var applied = await host.PostAsync(
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"count\",\"value\":\"3\"}]}",
            cookie);
        using var stale = await host.PostAsync(
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"enabled\",\"value\":\"true\"}]}",
            cookie);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await using (var observer = host.Context())
        {
            Assert.Equal(1, (await observer.Set<ServiceSettingEntity>().SingleAsync(Token)).Version);
            Assert.Single(await observer.Set<ManagementAuditLogEntity>().ToListAsync(Token));
        }

        await using var rollbackHost = await EfUpdateHost.StartAsync(new CommitBarrier(), CommitMode.RollbackAfterApply);
        var rollbackCookie = await rollbackHost.SignInAsync();
        using var rolledBack = await rollbackHost.PostAsync(
            "{\"expectedVersion\":0,\"changes\":[{\"key\":\"count\",\"value\":\"4\"}]}",
            rollbackCookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rolledBack.StatusCode);
        await using var rollbackObserver = rollbackHost.Context();
        Assert.Empty(await rollbackObserver.Set<ServiceSettingEntity>().ToListAsync(Token));
        Assert.Empty(await rollbackObserver.Set<ManagementAuditLogEntity>().ToListAsync(Token));
    }

    private enum CommitMode
    {
        Commit,
        RollbackAfterApply
    }

    private sealed class EfUpdateHost(
        WebApplication application,
        HttpClient client,
        string databasePath) : IAsyncDisposable
    {
        internal static async Task<EfUpdateHost> StartAsync(CommitBarrier barrier, CommitMode mode)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"sm-http-update-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(barrier);
            builder.Services.AddDbContext<UpdateHttpDbContext>((services, options) =>
                options.UseSqlite(connectionString).AddInterceptors(services.GetRequiredService<CommitBarrier>()));
            builder.Services.AddSingleton(new ServiceSettingDefinitionRegistry(
                [new Definitions()],
                [new PositiveCount()]));
            builder.Services.AddScoped<IServiceSettingUpdateTransaction>(services =>
                new EfCoreServiceSettingUpdateTransaction<UpdateHttpDbContext>(
                    services.GetRequiredService<UpdateHttpDbContext>()));
            builder.Services.AddScoped<ServiceSettingUpdateService>(services =>
                new ServiceSettingUpdateService(
                    Service,
                    services.GetRequiredService<ServiceSettingDefinitionRegistry>(),
                    services.GetRequiredService<IServiceSettingUpdateTransaction>()));
            var mantle = builder.Services.AddServiceMantle(
                Service,
                InstanceId.Parse("orders-01"),
                serviceVersion: "1.0");
            mantle.AddSensitiveHeaders();
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting();
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new ReadyHealthSource());

            var application = builder.Build();
            application.UseServiceMantlePipeline();
            application.MapPost("/sign-in", async (HttpContext context) =>
            {
                var identity = ManagementIdentity.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    "admin",
                    [ManagementPermission.Admin]);
                await context.SignInAsync(
                    ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                    identity.ToClaimsPrincipal());
                return Results.NoContent();
            }).AllowAnonymous();
            application.MapServiceMantleManagementApiV1().MapServiceMantleSettingUpdates(
                async (httpContext, command, cancellationToken) =>
                {
                    await using var scope = httpContext.RequestServices
                        .GetRequiredService<IServiceScopeFactory>()
                        .CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<UpdateHttpDbContext>();
                    await using var transaction = await context.Database
                        .BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        var result = await scope.ServiceProvider
                            .GetRequiredService<ServiceSettingUpdateService>()
                            .UpdateAsync(command, cancellationToken)
                            .ConfigureAwait(false);
                        if (!result.Succeeded)
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            return result;
                        }

                        if (mode == CommitMode.RollbackAfterApply)
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            return ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.StorageFailed);
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return result;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                });

            try
            {
                await using (var scope = application.Services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<UpdateHttpDbContext>()
                        .Database.EnsureCreatedAsync(Token);
                }

                await application.StartAsync(Token);
                return new EfUpdateHost(
                    application,
                    new HttpClient { BaseAddress = new Uri(application.Urls.Single()) },
                    databasePath);
            }
            catch
            {
                await application.DisposeAsync();
                File.Delete(databasePath);
                throw;
            }
        }

        internal UpdateHttpDbContext Context() => new(
            new DbContextOptionsBuilder<UpdateHttpDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);

        internal async Task<string> SignInAsync()
        {
            using var response = await client.PostAsync("/sign-in", content: null, Token);
            response.EnsureSuccessStatusCode();
            return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
        }

        internal Task<HttpResponseMessage> PostAsync(string body, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/management/v1/settings")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("x-correlation-id", "ef-setting-update");
            return client.SendAsync(request, Token);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
            File.Delete(databasePath);
        }
    }

    private sealed class CommitBarrier : DbTransactionInterceptor
    {
        private TaskCompletionSource? entered;
        private TaskCompletionSource? release;

        internal TaskCompletionSource Entered => entered ??
            throw new InvalidOperationException("The commit barrier is not armed.");

        internal void Arm()
        {
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => release?.TrySetResult();

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (entered is not null && release is not null)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                entered = null;
                release = null;
            }

            return result;
        }
    }

    private sealed class Definitions : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
        [
            new("name", ServiceSettingValueType.String),
            new("count", ServiceSettingValueType.Number, isRequired: true, defaultValue: "1"),
            new("enabled", ServiceSettingValueType.Boolean, defaultValue: "false"),
            new("payload", ServiceSettingValueType.Json),
            new("secret", ServiceSettingValueType.String, isSensitive: true)
        ];
    }

    private sealed class PositiveCount : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context) =>
            context.Values["count"].GetNumber() > 0
                ? []
                : [new ServiceSettingValidationError("count", "setting.count_positive")];
    }

    private sealed class UpdateHttpDbContext(DbContextOptions<UpdateHttpDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleSettings();
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
        }
    }

    private sealed class ReadyHealthSource : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable));
    }
}

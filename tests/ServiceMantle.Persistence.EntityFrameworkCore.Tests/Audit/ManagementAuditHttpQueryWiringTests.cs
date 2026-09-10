using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

/// <summary>
/// Proves the HTTP adapter resolves and executes the real scoped EF audit query service over a
/// consumer-owned DbContext rather than relying exclusively on endpoint stubs.
/// </summary>
public sealed class ManagementAuditHttpQueryWiringTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http_query_wires_scoped_EF_filter_keyset_tie_breaker_and_backfill_semantics()
    {
        await using var host = await EfAuditHost.StartAsync();
        var cookie = await host.SignInAsync();

        using var filtered = await host.GetAsync(
            "/management/v1/audit?action=configuration.changed&targetType=configuration" +
            "&targetId=smtp&operatorId=admin-1&pageSize=2&sortOrder=oldest",
            cookie);
        using var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var firstItems = filteredJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstItems.Length);
        Assert.Equal(3, filteredJson.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            firstItems.Select(item => item.GetProperty("id").GetString()).Order(),
            firstItems.Select(item => item.GetProperty("id").GetString()));
        var cursor = filteredJson.RootElement.GetProperty("continuationCursor").GetString();
        Assert.NotNull(cursor);

        using var second = await host.GetAsync(
            "/management/v1/audit?action=configuration.changed&targetType=configuration" +
            "&targetId=smtp&operatorId=admin-1&page=2&pageSize=2&sortOrder=oldest&cursor=" +
            Uri.EscapeDataString(cursor),
            cookie);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(secondJson.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Empty(firstItems.Select(item => item.GetProperty("id").GetString()).Intersect(
            secondJson.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())));

        using var mismatched = await host.GetAsync(
            "/management/v1/audit?action=admin_login.succeeded&page=2&pageSize=2" +
            "&sortOrder=oldest&cursor=" + Uri.EscapeDataString(cursor),
            cookie);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        using var beforeBackfill = await host.GetAsync(
            "/management/v1/audit?pageSize=2&sortOrder=oldest",
            cookie);
        using var beforeJson = JsonDocument.Parse(await beforeBackfill.Content.ReadAsStringAsync(Token));
        var backfillCursor = beforeJson.RootElement.GetProperty("continuationCursor").GetString();
        var originalCount = beforeJson.RootElement.GetProperty("totalCount").GetInt64();
        await host.InsertAsync("admin-backfill", Day(2));

        using var afterBackfill = await host.GetAsync(
            "/management/v1/audit?page=2&pageSize=2&sortOrder=oldest&cursor=" +
            Uri.EscapeDataString(backfillCursor!),
            cookie);
        using var afterJson = JsonDocument.Parse(await afterBackfill.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.OK, afterBackfill.StatusCode);
        Assert.Equal(originalCount + 1, afterJson.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            "admin-backfill",
            afterJson.RootElement.GetProperty("items")[0]
                .GetProperty("operator").GetProperty("operatorId").GetString());
    }

    private static DateTimeOffset Day(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    private sealed class EfAuditHost(
        WebApplication application,
        HttpClient client,
        SqliteConnection connection) : IAsyncDisposable
    {
        internal static async Task<EfAuditHost> StartAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(Token);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddDbContext<HttpAuditDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<IManagementAuditQueryService>(services =>
                new EfCoreManagementAuditQueryService<HttpAuditDbContext>(
                    services.GetRequiredService<HttpAuditDbContext>()));
            var mantle = builder.Services.AddServiceMantle(
                ServiceId.Parse("orders-api"),
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
                    ManagementSessionDefaults.AuthenticationScheme,
                    identity.ToClaimsPrincipal());
                return Results.NoContent();
            }).AllowAnonymous();
            application.MapServiceMantleManagementApiV1().MapServiceMantleAuditQueries();

            try
            {
                await using (var scope = application.Services.CreateAsyncScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<HttpAuditDbContext>();
                    await context.Database.EnsureCreatedAsync(Token);
                    var writer = new EfCoreManagementAuditWriter<HttpAuditDbContext>(context);
                    await WriteAsync(writer, "admin-1", Day(1));
                    await WriteAsync(writer, "admin-1", Day(1));
                    await WriteAsync(writer, "admin-1", Day(3));
                    await writer.RecordAsync(
                        ManagementAuditEvent.Create(
                            ManagementAuditOperator.Create(
                                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                                "admin-2"),
                            WellKnownManagementAuditActions.AdminLoginSucceeded,
                            ManagementAuditTarget.Create(
                                WellKnownManagementAuditTargetTypes.Service,
                                "orders-api"),
                            occurredAtUtc: Day(4)),
                        Token);
                    await context.SaveChangesAsync(Token);
                }

                await application.StartAsync(Token);
                return new EfAuditHost(
                    application,
                    new HttpClient { BaseAddress = new Uri(application.Urls.Single()) },
                    connection);
            }
            catch
            {
                await application.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        internal async Task<string> SignInAsync()
        {
            using var response = await client.PostAsync("/sign-in", content: null, Token);
            response.EnsureSuccessStatusCode();
            return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
        }

        internal Task<HttpResponseMessage> GetAsync(string path, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("x-correlation-id", "ef-audit-query");
            return client.SendAsync(request, Token);
        }

        internal async Task InsertAsync(string operatorId, DateTimeOffset occurredAtUtc)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<HttpAuditDbContext>();
            await WriteAsync(new EfCoreManagementAuditWriter<HttpAuditDbContext>(context), operatorId, occurredAtUtc);
            await context.SaveChangesAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static ValueTask<ManagementAuditRecord> WriteAsync(
            EfCoreManagementAuditWriter<HttpAuditDbContext> writer,
            string operatorId,
            DateTimeOffset occurredAtUtc) =>
            writer.RecordAsync(
                ManagementAuditEvent.Create(
                    ManagementAuditOperator.Create(
                        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                        operatorId),
                    WellKnownManagementAuditActions.ConfigurationChanged,
                    ManagementAuditTarget.Create(
                        WellKnownManagementAuditTargetTypes.Configuration,
                        "smtp"),
                    occurredAtUtc: occurredAtUtc),
                Token);
    }

    private sealed class HttpAuditDbContext(DbContextOptions<HttpAuditDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
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

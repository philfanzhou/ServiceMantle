using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.MsSql;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerDataProtectionCookieCollection
    : ICollectionFixture<SqlServerDataProtectionCookieFixture>
{
    public const string Name = "SQL Server Data Protection Cookie";
}

/// <summary>
/// Real SQL Server coverage for encrypted Data Protection persistence through the actual management
/// Cookie Authentication handler. Enable with RUN_SERVICEMANTLE_SQLSERVER_TESTS=true.
/// IXmlRepository is synchronous and has no caller cancellation surface, so these tests deliberately
/// do not manufacture an in-flight repository cancellation case.
/// </summary>
[Collection(SqlServerDataProtectionCookieCollection.Name)]
[RealDatabaseTest(RealDatabaseProvider.SqlServer)]
public sealed class SqlServerDataProtectionCookieTests(SqlServerDataProtectionCookieFixture fixture)
{
    private const string RootKey = "sqlserver-cookie-root-key-18f8a4d9d97c44f09d24";
    private const string OtherRootKey = "wrong-sqlserver-cookie-root-key-f140fb0bcedc4e4e";
    private const string OperatorId = "sensitive-sqlserver-cookie-operator";
    private static readonly ServiceId Service = ServiceId.Parse("cookie-sharing-service");

    [Fact]
    public async Task Same_service_and_root_key_share_real_management_cookies_in_both_directions()
    {
        await fixture.ResetAsync();
        await using var hostA = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "shared-a");
        await using var hostB = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "shared-b");

        var cookieA = await hostA.SignInAsync();
        await AssertAcceptedAsync(hostB, cookieA);
        var cookieB = await hostB.SignInAsync();
        await AssertAcceptedAsync(hostA, cookieB);

        var rows = await fixture.ReadRowsAsync(Service);
        Assert.NotEmpty(rows);
        Assert.All(rows, AssertEncryptedRow);
    }

    [Fact]
    public async Task Concurrent_cold_start_commits_readable_keys_and_preserves_bidirectional_cookies()
    {
        await fixture.ResetAsync();
        var barrier = new TwoActorBarrier(TimeSpan.FromSeconds(30));
        await using var hostA = await CookieHost.StartAsync(
            fixture.ConnectionString!,
            Service,
            RootKey,
            "cold-a",
            barrier.FirstActorAsync);
        await using var hostB = await CookieHost.StartAsync(
            fixture.ConnectionString!,
            Service,
            RootKey,
            "cold-b",
            barrier.SecondActorAsync);

        var cookies = await Task.WhenAll(hostA.SignInAsync(), hostB.SignInAsync());
        await AssertAcceptedAsync(hostB, cookies[0]);
        await AssertAcceptedAsync(hostA, cookies[1]);

        var rows = await fixture.ReadRowsAsync(Service);
        Assert.NotEmpty(rows);
        Assert.All(rows, AssertEncryptedRow);
        Assert.Equal(
            rows.Count,
            hostA.Services
                .GetRequiredService<EfCoreDataProtectionKeyRepository<SqlServerKeyDbContext>>()
                .GetAllElements()
                .Count);
        Assert.Equal(
            rows.Count,
            hostB.Services
                .GetRequiredService<EfCoreDataProtectionKeyRepository<SqlServerKeyDbContext>>()
                .GetAllElements()
                .Count);
    }

    [Fact]
    public async Task New_instance_loads_the_complete_ring_after_explicit_rotation()
    {
        await fixture.ResetAsync();
        await using var hostA = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "rotation-a");
        var originalCookie = await hostA.SignInAsync();
        var beforeRotation = await fixture.ReadRowsAsync(Service);

        var now = DateTimeOffset.UtcNow;
        hostA.Services.GetRequiredService<IKeyManager>()
            .CreateNewKey(now.AddMinutes(-1), now.AddDays(90));
        var afterRotation = await fixture.ReadRowsAsync(Service);
        Assert.True(afterRotation.Count > beforeRotation.Count);

        var rotatedCookie = await hostA.SignInAsync();
        await using var hostB = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "rotation-b");

        await AssertAcceptedAsync(hostB, originalCookie);
        await AssertAcceptedAsync(hostB, rotatedCookie);
        var cookieB = await hostB.SignInAsync();
        await AssertAcceptedAsync(hostA, cookieB);
        Assert.Equal(
            afterRotation.Count,
            hostB.Services.GetRequiredService<IKeyManager>().GetAllKeys().Count);
    }

    [Fact]
    public async Task Service_and_root_key_isolation_fail_closed_without_modifying_existing_rows()
    {
        await fixture.ResetAsync();
        await using var source = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "isolation-source");
        var cookie = await source.SignInAsync();
        var originalRows = await fixture.ReadRowsAsync(Service);
        var otherService = ServiceId.Parse("other-cookie-service");

        await using var otherServiceHost = await CookieHost.StartAsync(
            fixture.ConnectionString!, otherService, RootKey, "isolation-service");
        using var serviceResponse = await otherServiceHost.SendCookieAsync(cookie);
        await AssertRejectedSafelyAsync(
            serviceResponse,
            cookie,
            [
                fixture.ConnectionString!,
                SqlServerDataProtectionCookieFixture.DatabaseName,
                .. originalRows.Select(row => row.EncryptedXml),
            ]);
        Assert.Equal(originalRows, await fixture.ReadRowsAsync(Service));

        await using var wrongRootHost = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, OtherRootKey, "isolation-root");
        using var rootResponse = await wrongRootHost.SendCookieAsync(cookie);
        await AssertRejectedSafelyAsync(
            rootResponse,
            cookie,
            [
                fixture.ConnectionString!,
                SqlServerDataProtectionCookieFixture.DatabaseName,
                .. originalRows.Select(row => row.EncryptedXml),
            ]);
        Assert.Equal(originalRows, await fixture.ReadRowsAsync(Service));

        AssertSafeDiagnostics(
            wrongRootHost.Logs,
            [
                cookie,
                RootKey,
                OtherRootKey,
                fixture.ConnectionString!,
                SqlServerDataProtectionCookieFixture.DatabaseName,
                OperatorId,
                "sensitive-cookie-display-name",
                "<key",
                "encrypted_xml",
                .. originalRows.Select(row => row.EncryptedXml),
            ]);
    }

    [Fact]
    public async Task Damaged_ciphertext_fails_with_safe_http_and_diagnostics()
    {
        await fixture.ResetAsync();
        await using var source = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "damage-source");
        var cookie = await source.SignInAsync();
        const string damagedCiphertext = "sm:v1:damaged-ciphertext-secret";
        await fixture.DamageFirstRowAsync(Service, damagedCiphertext);
        var damagedRows = await fixture.ReadRowsAsync(Service);

        await using var reader = await CookieHost.StartAsync(
            fixture.ConnectionString!, Service, RootKey, "damage-reader");
        using var response = await reader.SendCookieAsync(cookie);

        Assert.Contains(response.StatusCode, new[]
        {
            HttpStatusCode.Unauthorized,
            HttpStatusCode.InternalServerError,
        });
        await AssertPublicProjectionExcludesAsync(
            response,
            [
                cookie,
                RootKey,
                damagedCiphertext,
                fixture.ConnectionString!,
                SqlServerDataProtectionCookieFixture.DatabaseName,
                OperatorId,
                "sensitive-cookie-display-name",
                "<key",
            ]);
        Assert.Equal(damagedRows, await fixture.ReadRowsAsync(Service));
        AssertSafeDiagnostics(
            reader.Logs,
            [
                cookie,
                RootKey,
                damagedCiphertext,
                fixture.ConnectionString!,
                SqlServerDataProtectionCookieFixture.DatabaseName,
                OperatorId,
                "sensitive-cookie-display-name",
                "<key",
            ]);
    }

    private static void AssertEncryptedRow(KeyRow row)
    {
        Assert.StartsWith("sm:v1:", row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<key", row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorId, row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, row.EncryptedXml, StringComparison.Ordinal);
    }

    private static async Task AssertAcceptedAsync(CookieHost host, string cookie)
    {
        using var response = await host.SendCookieAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
    }

    private static async Task AssertRejectedSafelyAsync(
        HttpResponseMessage response,
        string cookie,
        params string[] additionalForbidden)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(
            ServiceMantleManagementSessionDefaults.ExpiredErrorCode,
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(["errorCode"], document.RootElement.EnumerateObject().Select(item => item.Name));
        await AssertPublicProjectionExcludesAsync(
            response,
            [
                cookie,
                RootKey,
                OtherRootKey,
                OperatorId,
                "sensitive-cookie-display-name",
                "servicemantle.operator_id",
                "<key",
                "encrypted_xml",
                .. additionalForbidden,
            ]);
    }

    private static async Task AssertPublicProjectionExcludesAsync(
        HttpResponseMessage response,
        params string[] forbidden)
    {
        var projection = string.Join(
            '\n',
            response.Headers.SelectMany(header => header.Value.Prepend(header.Key))
                .Concat(response.Content.Headers.SelectMany(header => header.Value.Prepend(header.Key)))
                .Append(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, projection, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertSafeDiagnostics(
        IEnumerable<string> messages,
        params string[] forbidden)
    {
        var diagnostics = string.Join('\n', messages);
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class CookieHost(
        WebApplication application,
        HttpClient client,
        RecordingLoggerProvider logger) : IAsyncDisposable
    {
        internal IServiceProvider Services => application.Services;

        internal IReadOnlyCollection<string> Logs => logger.Messages;

        internal static async Task<CookieHost> StartAsync(
            string connectionString,
            ServiceId serviceId,
            string rootKey,
            string instanceSuffix,
            Func<CancellationToken, ValueTask>? beforeFirstProtection = null)
        {
            var logger = new RecordingLoggerProvider();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddProvider(logger);
            builder.Services.AddDbContextFactory<SqlServerKeyDbContext>(options =>
                options.UseSqlServer(connectionString));
            builder.Services
                .AddServiceMantle(serviceId, InstanceId.Parse($"cookie-{instanceSuffix}"))
                .AddManagementCookieAuthentication();
            builder.Services.AddDataProtection()
                .PersistKeysToServiceMantleEfCore<SqlServerKeyDbContext>(serviceId, _ => rootKey);

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            var firstProtection = 0;
            application.MapPost("/sign-in", async (HttpContext context) =>
            {
                if (beforeFirstProtection is not null && Interlocked.Exchange(ref firstProtection, 1) == 0)
                {
                    await beforeFirstProtection(context.RequestAborted);
                }

                var identity = ManagementIdentity.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    OperatorId,
                    [ManagementPermission.Admin],
                    "sensitive-cookie-display-name");
                await context.SignInAsync(
                    ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                    identity.ToClaimsPrincipal());
                return Results.NoContent();
            }).AllowAnonymous();
            application.MapGet("/management/accepted", () => Results.Json(new { status = "ok" }))
                .RequireServiceMantleManagementAdmin();

            await application.StartAsync(TestContext.Current.CancellationToken);
            var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
            return new CookieHost(application, client, logger);
        }

        internal async Task<string> SignInAsync()
        {
            using var response = await client.PostAsync(
                "/sign-in",
                content: null,
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            return setCookie.Split(';', 2)[0];
        }

        internal Task<HttpResponseMessage> SendCookieAsync(string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/management/accepted");
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.StopAsync(TestContext.Current.CancellationToken);
            await application.DisposeAsync();
            logger.Dispose();
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        internal IReadOnlyCollection<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(string.Join('\n', formatter(state, exception), exception?.ToString()));
        }
    }
}

public sealed class SqlServerDataProtectionCookieFixture : IAsyncLifetime
{
    internal const string DatabaseName = "servicemantle_data_protection";
    private MsSqlContainer? container;

    internal string? ConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.SqlServer))
        {
            return;
        }

        var image = Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLSERVER_IMAGE")
            ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
        container = new MsSqlBuilder(image).Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        ConnectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    internal async Task ResetAsync()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.SqlServer,
            ConnectionString is not null);
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    internal async Task<IReadOnlyList<KeyRow>> ReadRowsAsync(ServiceId serviceId)
    {
        await using var context = CreateContext();
        return await context.Set<DataProtectionKeyEntity>()
            .AsNoTracking()
            .Where(row => row.ServiceId == serviceId.Value)
            .OrderBy(row => row.KeyId)
            .Select(row => new KeyRow(row.ServiceId, row.KeyId, row.EncryptedXml))
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    internal async Task DamageFirstRowAsync(ServiceId serviceId, string ciphertext)
    {
        await using var context = CreateContext();
        var row = await context.Set<DataProtectionKeyEntity>()
            .Where(row => row.ServiceId == serviceId.Value)
            .OrderBy(row => row.KeyId)
            .FirstAsync(TestContext.Current.CancellationToken);
        row.EncryptedXml = ciphertext;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private SqlServerKeyDbContext CreateContext() => new(
        new DbContextOptionsBuilder<SqlServerKeyDbContext>()
            .UseSqlServer(ConnectionString!)
            .Options);
}

internal sealed record KeyRow(string ServiceId, string KeyId, string EncryptedXml);

public sealed class SqlServerKeyDbContext(DbContextOptions<SqlServerKeyDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddServiceMantleDataProtectionKeys();
}

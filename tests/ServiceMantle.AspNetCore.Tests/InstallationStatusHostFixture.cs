using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the opt-in anonymous installation status entry and owns everything it
/// creates, including the temporary local Bootstrap file the entry observes.
/// </summary>
internal sealed class InstallationStatusHostFixture : IAsyncDisposable
{
    internal const string SentinelProvider = "SentinelProvider";
    internal const string SentinelServerVersion = "sentinel-server-version";
    internal const string SentinelConnectionString = "Host=db.internal;Password=sentinel-connection-secret";
    internal const string SentinelMasterKey = "sentinel-master-key";

    internal static readonly ServiceId Service = ServiceId.Parse("catalog");

    private readonly List<CancellationTokenSource> aborts = [];
    private readonly TemporaryDirectory directory;
    private WebApplication? application;
    private HttpClient? client;

    private InstallationStatusHostFixture(
        WebApplication application,
        TemporaryDirectory directory,
        string root,
        string bootstrapFilePath)
    {
        this.application = application;
        this.directory = directory;
        Root = root;
        BootstrapFilePath = bootstrapFilePath;
    }

    internal string Root { get; }

    internal string BootstrapFilePath { get; }

    internal string StatusPath => Root + ServiceMantleManagementEntryDefaults.StatusPath;

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    internal ServiceMantleBootstrapRestartLatch Latch =>
        Application.Services.GetRequiredService<ServiceMantleBootstrapRestartLatch>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Writes a valid local Bootstrap file that carries only sentinel secrets.</summary>
    internal static void WriteBootstrapFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new BootstrapFileStore(Service, new BootstrapDatabaseProviderRegistry([]), path)
            .Create(new BootstrapConfiguration(
                Service,
                new BootstrapDatabaseConfiguration(
                    SentinelProvider,
                    SentinelServerVersion,
                    SentinelConnectionString),
                SentinelMasterKey));
    }

    internal static async Task<InstallationStatusHostFixture> CreateAsync(
        IServiceHealthSnapshotSource? source = null,
        string? root = null,
        bool status = true,
        bool managementApi = true,
        bool entries = true,
        int mapCount = 1,
        string bootstrapFile = "absent",
        IServiceMantleBootstrapStatusReader? bootstrapReader = null,
        TimeSpan? snapshotTimeout = null,
        int managementPermitLimit = 120,
        Action<WebApplication, string>? extra = null)
    {
        var directory = new TemporaryDirectory();
        var resolvedRoot = root ?? ServiceMantleManagementApiDefaults.DefaultRootPath;
        var bootstrapFilePath = Path.Combine(directory.Path, "config", "catalog.bootstrap.json");
        switch (bootstrapFile)
        {
            case "valid":
                WriteBootstrapFile(bootstrapFilePath);
                break;
            case "damaged":
                Directory.CreateDirectory(Path.GetDirectoryName(bootstrapFilePath)!);
                await File.WriteAllTextAsync(bootstrapFilePath, "{ not json", Token);
                break;
        }

        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            Service,
            InstanceId.Parse("catalog-01"),
            bootstrapFilePath,
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders();
        mantle.AddSecurityResponseHeaders();
        mantle.AddRateLimiting(options => options.Management.PermitLimit = managementPermitLimit);
        mantle.AddManagementCookieAuthentication();
        if (managementApi)
        {
            mantle.AddServiceMantleManagementApiV1(options =>
            {
                options.RootPath = resolvedRoot;
                if (snapshotTimeout is { } timeout)
                {
                    options.SnapshotTimeout = timeout;
                }
            });
        }

        if (entries)
        {
            mantle.AddServiceMantleManagementEntries();
        }

        // Registered first so the capability's own TryAdd keeps the substituted boundary.
        if (bootstrapReader is not null)
        {
            builder.Services.AddSingleton(bootstrapReader);
        }

        if (status)
        {
            mantle.AddServiceMantleInstallationStatus();
        }

        if (source is not null)
        {
            builder.Services.AddSingleton(source);
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            for (var index = 0; index < mapCount; index++)
            {
                application.MapServiceMantleInstallationStatus();
            }

            extra?.Invoke(application, resolvedRoot);
        }
        catch (Exception)
        {
            await using (application.ConfigureAwait(false))
            {
            }

            directory.Dispose();
            throw;
        }

        return new InstallationStatusHostFixture(application, directory, resolvedRoot, bootstrapFilePath);
    }

    internal static async Task<InstallationStatusHostFixture> StartAsync(
        IServiceHealthSnapshotSource? source = null,
        string? root = null,
        string bootstrapFile = "absent",
        IServiceMantleBootstrapStatusReader? bootstrapReader = null,
        TimeSpan? snapshotTimeout = null,
        int managementPermitLimit = 120,
        Action<WebApplication, string>? extra = null)
    {
        var fixture = await CreateAsync(
            source: source ?? new CountingSnapshotSource(Snapshot(ServiceStartupPhase.BootstrapConfiguration)),
            root: root,
            bootstrapFile: bootstrapFile,
            bootstrapReader: bootstrapReader,
            snapshotTimeout: snapshotTimeout,
            managementPermitLimit: managementPermitLimit,
            extra: extra);
        await fixture.StartAsync();
        return fixture;
    }

    internal async Task StartAsync()
    {
        await Application.StartAsync(Token);
        client = Application.GetTestClient();
    }

    internal static ServiceHealthSnapshot Snapshot(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migrationStatus = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState databaseStatus = ServiceDatabaseReadinessState.Reachable) =>
        new(phase, migrationStatus, databaseStatus);

    internal CancellationTokenSource CreateAbort()
    {
        var abort = new CancellationTokenSource();
        aborts.Add(abort);
        return abort;
    }

    internal Task<HttpResponseMessage> SendAsync(
        HttpMethod? method = null,
        string? path = null,
        CancellationTokenSource? abort = null) =>
        Client.SendAsync(
            new HttpRequestMessage(method ?? HttpMethod.Get, path ?? StatusPath),
            HttpCompletionOption.ResponseContentRead,
            abort?.Token ?? Token);

    public async ValueTask DisposeAsync()
    {
        foreach (var abort in aborts)
        {
            await abort.CancelAsync();
        }

        client?.Dispose();
        client = null;
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }

        foreach (var abort in aborts)
        {
            abort.Dispose();
        }

        directory.Dispose();
    }

    /// <summary>Counts snapshot reads and can hold one request inside the source.</summary>
    internal sealed class CountingSnapshotSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        private int calls;
        private ServiceHealthSnapshot current = snapshot;

        internal int Calls => Volatile.Read(ref calls);

        internal ServiceHealthSnapshot Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        /// <summary>Set to hold every call until the returned gate is released.</summary>
        internal TaskCompletionSource? Gate { get; set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return Current;
        }
    }

    /// <summary>Fails every snapshot read with an exception carrying a sentinel value.</summary>
    internal sealed class FailingSnapshotSource(Exception failure) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ServiceHealthSnapshot>(failure);
    }

    /// <summary>Returns one snapshot per call, cycling through a fixed sequence.</summary>
    internal sealed class SequenceSnapshotSource(params ServiceHealthSnapshot[] snapshots)
        : IServiceHealthSnapshotSource
    {
        private int calls;

        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshots[(Interlocked.Increment(ref calls) - 1) % snapshots.Length]);
    }

    /// <summary>Counts Bootstrap boundary reads and answers a fixed outcome.</summary>
    internal sealed class CountingBootstrapStatusReader(bool configured)
        : IServiceMantleBootstrapStatusReader
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        public bool IsBootstrapConfigured()
        {
            Interlocked.Increment(ref calls);
            return configured;
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ServiceMantle.AspNetCore.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

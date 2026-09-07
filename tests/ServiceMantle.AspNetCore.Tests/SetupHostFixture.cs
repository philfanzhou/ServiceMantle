using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Starts a host around the opt-in Setup entries with a recording installation store and a
/// recording completion executor, and owns everything it creates.
/// </summary>
internal sealed class SetupHostFixture : IAsyncDisposable
{
    /// <summary>A syntactically valid Setup Code used wherever the value must not be echoed.</summary>
    internal const string SentinelCode = "SentinelSetupCode0123456789_-ABC";

    private readonly List<CancellationTokenSource> aborts = [];
    private WebApplication? application;
    private HttpClient? client;

    private SetupHostFixture(
        WebApplication application,
        RecordingInstallationStore store,
        RecordingExecutor executor,
        string root)
    {
        this.application = application;
        Store = store;
        Executor = executor;
        Root = root;
    }

    internal RecordingInstallationStore Store { get; }

    internal RecordingExecutor Executor { get; }

    internal string Root { get; }

    internal string SetupPath => Root + ServiceMantleManagementEntryDefaults.SetupPath;

    internal WebApplication Application =>
        application ?? throw new InvalidOperationException("The host was disposed.");

    internal HttpClient Client =>
        client ?? throw new InvalidOperationException("The host was not started.");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<SetupHostFixture> CreateAsync(
        InstallationStatus? installed = InstallationStatus.PendingSetup,
        ServiceMantleSetupCompletionResult? completion = null,
        ServiceStartupPhase phase = ServiceStartupPhase.PendingSetup,
        ServiceMigrationReadinessState migrationStatus = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState databaseStatus = ServiceDatabaseReadinessState.Reachable,
        string? root = null,
        bool registerStore = true,
        bool mapExecutor = true,
        int mapCount = 1,
        int setupPermitLimit = 60)
    {
        var resolvedRoot = root ?? ServiceMantleManagementApiDefaults.DefaultRootPath;
        var store = new RecordingInstallationStore(installed);
        var executor = new RecordingExecutor(completion ?? ServiceMantleSetupCompletionResult.Committed());
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0");
        mantle.AddSensitiveHeaders();
        mantle.AddSecurityResponseHeaders();
        mantle.AddRateLimiting(options => options.Setup.PermitLimit = setupPermitLimit);
        mantle.AddManagementCookieAuthentication();
        mantle.AddServiceMantleManagementApiV1(options => options.RootPath = resolvedRoot);
        mantle.AddServiceMantleManagementEntries();
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(
            new FixedHealthSource(new ServiceHealthSnapshot(phase, migrationStatus, databaseStatus)));
        if (registerStore)
        {
            builder.Services.AddScoped<IServiceInstallationStore>(_ => store);
        }

        var application = builder.Build();
        try
        {
            application.UseServiceMantlePipeline();
            for (var index = 0; index < mapCount; index++)
            {
                application.MapServiceMantleSetup(mapExecutor ? executor.Execute : null);
            }
        }
        catch (Exception)
        {
            await using (application.ConfigureAwait(false))
            {
            }

            throw;
        }

        return new SetupHostFixture(application, store, executor, resolvedRoot);
    }

    internal static async Task<SetupHostFixture> StartAsync(
        InstallationStatus? installed = InstallationStatus.PendingSetup,
        ServiceMantleSetupCompletionResult? completion = null,
        ServiceStartupPhase phase = ServiceStartupPhase.PendingSetup,
        ServiceMigrationReadinessState migrationStatus = ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState databaseStatus = ServiceDatabaseReadinessState.Reachable,
        string? root = null,
        bool registerStore = true,
        int setupPermitLimit = 60)
    {
        var fixture = await CreateAsync(
            installed: installed,
            completion: completion,
            phase: phase,
            migrationStatus: migrationStatus,
            databaseStatus: databaseStatus,
            root: root,
            registerStore: registerStore,
            setupPermitLimit: setupPermitLimit);
        await fixture.StartAsync();
        return fixture;
    }

    internal async Task StartAsync()
    {
        await Application.StartAsync(Token);
        client = Application.GetTestClient();
    }

    internal CancellationTokenSource CreateAbort()
    {
        var abort = new CancellationTokenSource();
        aborts.Add(abort);
        return abort;
    }

    internal Task<HttpResponseMessage> ReadAsync(HttpMethod? method = null, string? path = null) =>
        Client.SendAsync(
            new HttpRequestMessage(method ?? HttpMethod.Get, path ?? SetupPath),
            Token);

    /// <summary>Sends one conforming completion request unless a part is deliberately replaced.</summary>
    internal Task<HttpResponseMessage> CompleteAsync(
        string? body = null,
        string? contentType = "application/json",
        string? path = null,
        string?[]? unsafeHeader = null,
        string? contentEncoding = null,
        CancellationTokenSource? abort = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path ?? SetupPath);
        foreach (var value in unsafeHeader ?? [ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderValue])
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderName,
                    value);
            }
        }

        var content = new ByteArrayContent(
            Encoding.UTF8.GetBytes(body ?? "{\"code\":\"" + SentinelCode + "\"}"));
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        request.Content = content;
        return Client.SendAsync(request, abort?.Token ?? Token);
    }

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
    }

    /// <summary>Answers one fixed installation state and counts reads.</summary>
    internal sealed class RecordingInstallationStore(InstallationStatus? status) : IServiceInstallationStore
    {
        private int reads;

        internal int Reads => Volatile.Read(ref reads);

        /// <summary>Set to fail every read with this exception.</summary>
        internal Exception? Failure { get; set; }

        internal InstallationStatus? Status { get; set; } = status;

        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure)
            {
                return ValueTask.FromException<ServiceInstallationState?>(failure);
            }

            return ValueTask.FromResult(Status switch
            {
                null => null,
                InstallationStatus.Completed => ServiceInstallationState
                    .CreatePending(serviceId)
                    .Complete(),
                _ => ServiceInstallationState.CreatePending(serviceId),
            });
        }

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Records the codes it received and answers one fixed completion result.</summary>
    internal sealed class RecordingExecutor(ServiceMantleSetupCompletionResult result)
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal List<string> Codes { get; } = [];

        /// <summary>Set to throw instead of answering.</summary>
        internal Exception? Failure { get; set; }

        /// <summary>Set to hold every call until the gate is released.</summary>
        internal TaskCompletionSource? Gate { get; set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ServiceMantleSetupCompletionResult? Result { get; set; } = result;

        /// <summary>Selects a result from the 1-based call number, overriding <see cref="Result"/>.</summary>
        internal Func<int, ServiceMantleSetupCompletionResult?>? Selector { get; set; }

        /// <summary>The number of concurrent calls that must arrive before <see cref="Entered"/> completes.</summary>
        internal int ExpectedCalls { get; set; } = 1;

        internal async ValueTask<ServiceMantleSetupCompletionResult> Execute(
            Microsoft.AspNetCore.Http.HttpContext httpContext,
            SetupCode setupCode,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            lock (Codes)
            {
                Codes.Add(setupCode.Reveal());
            }

            if (call >= ExpectedCalls)
            {
                Entered.TrySetResult();
            }

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return (Selector is null ? Result : Selector(call))!;
        }
    }

    private sealed class FixedHealthSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }
}

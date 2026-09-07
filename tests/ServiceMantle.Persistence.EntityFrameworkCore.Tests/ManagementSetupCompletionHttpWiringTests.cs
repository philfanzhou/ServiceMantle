using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Drives the Setup entries over real HTTP against a real SQLite database, so that "committed"
/// means a second connection can see the commit.
/// </summary>
public sealed class ManagementSetupCompletionHttpWiringTests
{
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_completion_answers_204_only_after_commit_and_persists_everything_once()
    {
        var barrier = new CommitBarrier();
        await using var host = await SetupHost.StartAsync(barrier);
        var code = await host.IssueCodeAsync();
        var issuedVersion = await host.VersionAsync();
        barrier.Arm();

        using var pending = await host.ReadAsync();
        Assert.Equal("{\"status\":\"pending\"}", await pending.Content.ReadAsStringAsync(Token));

        var completion = host.CompleteAsync(code);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // Nothing is visible to another connection, and no 204 has been produced, before the commit.
        Assert.False(completion.IsCompleted);
        await using (var beforeCommit = host.Context())
        {
            var row = await beforeCommit.ServiceInstallations.AsNoTracking().SingleAsync(Token);
            Assert.Equal(InstallationStatus.PendingSetup, row.Status);
            Assert.Empty(await beforeCommit.Set<SetupNoteEntity>().AsNoTracking().ToListAsync(Token));
        }

        barrier.Release();
        using var response = await completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));
        await using var observer = host.Context();
        var installation = await observer.ServiceInstallations.AsNoTracking().SingleAsync(Token);
        Assert.Equal(InstallationStatus.Completed, installation.Status);
        Assert.NotNull(installation.CompletedAtUtc);
        // The code material is cleared while the issuance generation is retained.
        Assert.Null(installation.SetupCodeDigest);
        Assert.Null(installation.SetupCodeIssuedAtUtc);
        Assert.Null(installation.SetupCodeExpiresAtUtc);
        Assert.Equal(1, installation.SetupCodeGeneration);
        Assert.Equal(issuedVersion + 1, installation.Version);
        Assert.Equal(["contributor-a", "contributor-b"], await observer.Set<SetupNoteEntity>()
            .AsNoTracking()
            .OrderBy(note => note.Name)
            .Select(note => note.Name)
            .ToListAsync(Token));

        using var afterwards = await host.ReadAsync();
        Assert.Equal("{\"status\":\"completed\"}", await afterwards.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_invalid_code_is_rejected_before_any_contributor_stages_anything()
    {
        await using var host = await SetupHost.StartAsync(new CommitBarrier());
        await host.IssueCodeAsync();

        using var wrongCode = await host.CompleteAsync("ThisIsNotTheIssuedSetupCode00000");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongCode.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"management.setup.credential_invalid\"}",
            await wrongCode.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, host.Contributors.Registrations);
        await using var observer = host.Context();
        Assert.Equal(
            InstallationStatus.PendingSetup,
            (await observer.ServiceInstallations.AsNoTracking().SingleAsync(Token)).Status);
        Assert.Empty(await observer.Set<SetupNoteEntity>().AsNoTracking().ToListAsync(Token));
    }

    [Theory]
    [InlineData(FailureMode.ContributorRejects, HttpStatusCode.BadRequest)]
    [InlineData(FailureMode.ContributorThrows, HttpStatusCode.BadRequest)]
    [InlineData(FailureMode.CommitFails, HttpStatusCode.ServiceUnavailable)]
    public async Task Every_failure_after_staging_rolls_back_and_leaves_no_partial_state(
        FailureMode mode,
        HttpStatusCode expected)
    {
        await using var host = await SetupHost.StartAsync(new CommitBarrier(), mode);
        var code = await host.IssueCodeAsync();
        var issuedVersion = await host.VersionAsync();

        using var response = await host.CompleteAsync(code);

        Assert.Equal(expected, response.StatusCode);
        await using var observer = host.Context();
        var installation = await observer.ServiceInstallations.AsNoTracking().SingleAsync(Token);
        Assert.Equal(InstallationStatus.PendingSetup, installation.Status);
        Assert.Null(installation.CompletedAtUtc);
        // The code is still outstanding, so a retry with a fixed consumer remains possible.
        Assert.NotNull(installation.SetupCodeDigest);
        Assert.Equal(issuedVersion, installation.Version);
        Assert.Empty(await observer.Set<SetupNoteEntity>().AsNoTracking().ToListAsync(Token));
    }

    [Fact]
    public async Task Two_overlapping_completions_produce_one_204_and_one_stable_conflict()
    {
        await using var host = await SetupHost.StartAsync(new CommitBarrier());
        var code = await host.IssueCodeAsync();
        // Both requests are in flight and both already read a pending installation. The gate then
        // fixes the interleaving so the assertion cannot depend on database lock timing.
        host.HoldExecutors = true;

        var first = host.CompleteAsync(code);
        var second = host.CompleteAsync(code);
        await host.BothEnteredExecutor.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        // The winner runs to completion first; only then does the loser open its own transaction,
        // so the assertion depends on the contract rather than on SQLite write-lock timing.
        host.ReleaseArrival(0);
        await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(20), Token);
        host.ReleaseArrival(1);

        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(20), Token);
        using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(20), Token);
        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };

        Assert.Single(statuses, status => status == HttpStatusCode.NoContent);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        await using var observer = host.Context();
        Assert.Equal(
            InstallationStatus.Completed,
            (await observer.ServiceInstallations.AsNoTracking().SingleAsync(Token)).Status);
        // The contributors staged exactly once, so the losing attempt left nothing behind.
        Assert.Equal(2, await observer.Set<SetupNoteEntity>().AsNoTracking().CountAsync(Token));

        // A replay is then a stable conflict that never parses the code again.
        using var replay = await host.CompleteAsync(code);
        using var garbage = await host.CompleteAsync("not-a-code");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, garbage.StatusCode);
    }

    public enum FailureMode
    {
        None,
        ContributorRejects,
        ContributorThrows,
        CommitFails,
    }

    private sealed class SetupHost(
        WebApplication application,
        HttpClient client,
        string databasePath,
        RecordingContributors contributors) : IAsyncDisposable
    {
        private readonly List<TaskCompletionSource> executorArrivals = [];

        internal RecordingContributors Contributors { get; } = contributors;

        /// <summary>Set to hold every executor at its entry, before it opens a transaction.</summary>
        internal bool HoldExecutors { get; set; }

        internal TaskCompletionSource BothEnteredExecutor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static async Task<SetupHost> StartAsync(
            CommitBarrier barrier,
            FailureMode mode = FailureMode.None)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"sm-http-setup-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";
            var contributors = new RecordingContributors(mode);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(barrier);
            builder.Services.AddSingleton(contributors);
            builder.Services.AddDbContext<SetupHttpDbContext>((services, options) => options
                .UseSqlite(connectionString)
                .AddInterceptors(services.GetRequiredService<CommitBarrier>()));
            builder.Services.AddScoped<IServiceInstallationStore>(services =>
                new EfCoreServiceInstallationStore<SetupHttpDbContext>(
                    services.GetRequiredService<SetupHttpDbContext>()));
            var mantle = builder.Services.AddServiceMantle(
                Service,
                InstanceId.Parse("orders-01"),
                serviceVersion: "1.0");
            mantle.AddSensitiveHeaders();
            mantle.AddSecurityResponseHeaders();
            mantle.AddRateLimiting();
            mantle.AddManagementCookieAuthentication();
            mantle.AddServiceMantleManagementApiV1();
            mantle.AddServiceMantleManagementEntries();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new PendingSetupHealthSource());

            var application = builder.Build();
            SetupHost? host = null;
            application.UseServiceMantlePipeline();
            application.MapServiceMantleSetup((httpContext, setupCode, cancellationToken) =>
                host!.ExecuteAsync(httpContext, setupCode, mode, cancellationToken));

            try
            {
                await using (var scope = application.Services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<SetupHttpDbContext>()
                        .Database.EnsureCreatedAsync(Token);
                }

                await application.StartAsync(Token);
                host = new SetupHost(
                    application,
                    new HttpClient { BaseAddress = new Uri(application.Urls.Single()) },
                    databasePath,
                    contributors);
                return host;
            }
            catch
            {
                await application.DisposeAsync();
                File.Delete(databasePath);
                throw;
            }
        }

        internal SetupHttpDbContext Context() => new(
            new DbContextOptionsBuilder<SetupHttpDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);

        /// <summary>Creates the pending installation and issues the one real Setup Code.</summary>
        internal async Task<string> IssueCodeAsync()
        {
            await using var context = Context();
            await new EfCoreServiceInstallationStore<SetupHttpDbContext>(context)
                .CreatePendingAsync(Service, Token);
            var issued = await new EfCoreServiceSetupCodeStore<SetupHttpDbContext>(context)
                .CreateAsync(Service, Token);
            return issued.SetupCode!.Reveal();
        }

        internal async Task<int> VersionAsync()
        {
            await using var context = Context();
            return (await context.ServiceInstallations.AsNoTracking().SingleAsync(Token)).Version;
        }

        internal Task<HttpResponseMessage> ReadAsync() =>
            client.GetAsync("/management/v1/setup", Token);

        internal Task<HttpResponseMessage> CompleteAsync(string code)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/management/v1/setup")
            {
                Content = new StringContent(
                    "{\"code\":\"" + code + "\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Add(
                ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderName,
                ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderValue);
            return client.SendAsync(request, Token);
        }

        /// <summary>Releases one held executor by its arrival position.</summary>
        internal void ReleaseArrival(int index)
        {
            lock (executorArrivals)
            {
                executorArrivals[index].TrySetResult();
            }
        }

        /// <summary>
        /// The consumer transaction boundary: a fresh scope, a clean DbContext, read-only
        /// validation, orchestration, staged consumption, one save, and a commit.
        /// </summary>
        private async ValueTask<ServiceMantleSetupCompletionResult> ExecuteAsync(
            HttpContext httpContext,
            SetupCode setupCode,
            FailureMode mode,
            CancellationToken cancellationToken)
        {
            await HoldAsync(cancellationToken).ConfigureAwait(false);
            await using var scope = httpContext.RequestServices
                .GetRequiredService<IServiceScopeFactory>()
                .CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<SetupHttpDbContext>();
            var codeStore = new EfCoreServiceSetupCodeStore<SetupHttpDbContext>(context);
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var validation = await codeStore
                    .ValidateAsync(Service, setupCode.Reveal(), cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Map(validation.ErrorCode);
                }

                var orchestration = await new ServiceSetupOrchestrator(
                        Contributors.Create(context),
                        new EfStagingScope(context))
                    .OrchestrateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!orchestration.Succeeded)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return orchestration.ErrorCode == WellKnownServiceSetupErrorCodes.CleanupFailed
                        ? ServiceMantleSetupCompletionResult.Unavailable()
                        : ServiceMantleSetupCompletionResult.ValidationFailed();
                }

                var consumption = await codeStore
                    .StageConsumeAsync(Service, setupCode.Reveal(), cancellationToken)
                    .ConfigureAwait(false);
                if (!consumption.IsStaged)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Map(consumption.ErrorCode);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (mode == FailureMode.CommitFails)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ServiceMantleSetupCompletionResult.Unavailable();
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ServiceMantleSetupCompletionResult.Committed();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ServiceMantleSetupCompletionResult.Unavailable();
            }
        }

        private async Task HoldAsync(CancellationToken cancellationToken)
        {
            if (!HoldExecutors)
            {
                return;
            }

            var arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int arrivals;
            lock (executorArrivals)
            {
                executorArrivals.Add(arrival);
                arrivals = executorArrivals.Count;
            }

            if (arrivals >= 2)
            {
                BothEnteredExecutor.TrySetResult();
            }

            await arrival.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
            File.Delete(databasePath);
        }

        private static ServiceMantleSetupCompletionResult Map(string? errorCode) => errorCode switch
        {
            WellKnownSetupCodeErrorCodes.InstallationCompleted or
                WellKnownSetupCodeErrorCodes.ConcurrencyConflict =>
                ServiceMantleSetupCompletionResult.Conflict(),
            WellKnownSetupCodeErrorCodes.Invalid or
                WellKnownSetupCodeErrorCodes.Expired or
                WellKnownSetupCodeErrorCodes.NotCreated or
                WellKnownSetupCodeErrorCodes.SetupCodeRequired =>
                ServiceMantleSetupCompletionResult.CredentialInvalid(),
            _ => ServiceMantleSetupCompletionResult.Unavailable(),
        };
    }

    private sealed class EfStagingScope(SetupHttpDbContext context) : IServiceSetupStagingScope
    {
        public bool HasPendingChanges => context.ChangeTracker.HasChanges();

        public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
        {
            context.ChangeTracker.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingContributors(FailureMode mode)
    {
        private int registrations;

        internal int Registrations => Volatile.Read(ref registrations);

        internal IEnumerable<IServiceSetupContributor> Create(SetupHttpDbContext context) =>
        [
            new NoteContributor(1, "contributor-a", context, this, FailureMode.None),
            new NoteContributor(2, "contributor-b", context, this, mode),
        ];

        internal void Record() => Interlocked.Increment(ref registrations);
    }

    private sealed class NoteContributor(
        int order,
        string name,
        SetupHttpDbContext context,
        RecordingContributors recorder,
        FailureMode mode) : IServiceSetupContributor
    {
        public int Order => order;

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceSetupContributorResult.Success());

        public ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default)
        {
            recorder.Record();
            context.Set<SetupNoteEntity>().Add(new SetupNoteEntity { Name = name });
            return mode switch
            {
                FailureMode.ContributorRejects => ValueTask.FromResult(
                    ServiceSetupContributorResult.Rejected("setup.note_rejected")),
                FailureMode.ContributorThrows => ValueTask.FromException<ServiceSetupContributorResult>(
                    new InvalidOperationException("contributor secret detail")),
                _ => ValueTask.FromResult(ServiceSetupContributorResult.Success()),
            };
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

    private sealed class PendingSetupHealthSource : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ServiceHealthSnapshot(
                ServiceStartupPhase.PendingSetup,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable));
    }

    private sealed class SetupNoteEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class SetupHttpDbContext(DbContextOptions<SetupHttpDbContext> options)
        : DbContext(options), IServiceMantleDbContext
    {
        public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleInstallation();
            modelBuilder.Entity<SetupNoteEntity>(entity =>
            {
                entity.ToTable("setup_notes");
                entity.HasKey(note => note.Id);
                entity.Property(note => note.Name).HasMaxLength(64).IsRequired();
            });
        }
    }
}

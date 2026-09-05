using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

public sealed class SqliteBootstrapDatabaseProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ServiceId Service = ServiceId.Parse("sqlite-bootstrap");
    private static readonly InstanceId Instance = InstanceId.Parse("sqlite-bootstrap-01");
    private static string PrivatePath => OperatingSystem.IsWindows()
        ? @"C:\private-sqlite-target\secret.db"
        : "/private-sqlite-target/secret.db";

    [Fact]
    public void Descriptor_declares_canonical_file_provider_without_aliases_or_server_version()
    {
        var descriptor = new SqliteBootstrapDatabaseProvider().Descriptor;
        Assert.Equal("SQLite", descriptor.Id);
        Assert.Equal("SQLite", descriptor.DisplayName);
        Assert.Equal(BootstrapDatabaseTargetKind.File, descriptor.TargetKind);
        Assert.Equal(BootstrapServerVersionRequirement.Forbidden, descriptor.ServerVersionRequirement);
        Assert.Empty(descriptor.Aliases);
    }

    [Theory]
    [InlineData("PostgreSQL", null, "database.provider_mismatch")]
    [InlineData("SQLite", "3.46", "database.server_version_not_allowed")]
    [InlineData("SQLite", null, "database.connection_string_invalid")]
    public async Task Invalid_metadata_or_connection_string_fails_before_IO(
        string providerId, string? version, string expectedCode)
    {
        var fileSystem = new ProbeFileSystem();
        var databaseAccess = new ProbeDatabaseAccess();
        var provider = CreateProvider(fileSystem, databaseAccess);
        var target = new BootstrapDatabaseConfiguration(providerId, version, "Unknown=Password-secret");

        AssertSafeResult(await provider.ValidateAsync(target, Token), target, expectedCode);
        Assert.Equal(0, fileSystem.InspectCalls);
        Assert.Equal(0, databaseAccess.InspectCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Registration_is_opt_in_idempotent_and_independent_of_preparation_and_deployment(
        bool registerBootstrap, bool registerPreparation)
    {
        var services = new ServiceCollection();
        var builder = services.AddServiceMantle(Service, Instance);
        if (registerBootstrap)
        {
            builder.AddBootstrapDatabaseProvider<SqliteBootstrapDatabaseProvider>();
            builder.AddBootstrapDatabaseProvider<SqliteBootstrapDatabaseProvider>();
        }

        if (registerPreparation)
        {
            builder.AddDatabaseTargetPreparationProvider<SqliteDatabaseTargetPreparationProvider>();
        }

        using var container = services.BuildServiceProvider();
        var registry = container.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        Assert.Equal(registerBootstrap ? 1 : 0, container.GetServices<IBootstrapDatabaseProvider>().Count());
        Assert.Equal(registerBootstrap, registry.TryGetProvider("sQlItE", out var resolved));
        if (registerBootstrap)
        {
            Assert.IsType<SqliteBootstrapDatabaseProvider>(resolved);
            Assert.Equal("SQLite", registry.ProviderIdResolver.Canonicalize("sQlItE"));
        }

        Assert.Equal(registerPreparation, container.GetRequiredService<DatabaseTargetPreparationProviderRegistry>()
            .TryGetProvider("SQLite", out _));
        Assert.Empty(container.GetServices<IDatabaseDeploymentCapabilityProvider>());

        var validator = container.GetRequiredService<IBootstrapCandidateValidator>();
        var candidate = new BootstrapConfiguration(Service,
            new BootstrapDatabaseConfiguration("sQlItE", "3.46", "Data Source=:memory:"), "master-key");
        var result = await validator.ValidateAsync(candidate, Token);
        Assert.Equal(registerBootstrap ? "database.server_version_not_allowed" : "database.provider_not_registered",
            result.ErrorCode);
    }

    public static TheoryData<int, int, int, string?> ObservationCases => new()
    {
        { (int)SqlitePathInspectionStatus.MissingFile, 0, 0, "database.target_not_found" },
        { (int)SqlitePathInspectionStatus.ParentMissing, 0, 0, "database.connection_string_invalid" },
        { (int)SqlitePathInspectionStatus.InvalidTarget, 0, 0, "database.connection_string_invalid" },
        { (int)SqlitePathInspectionStatus.PermissionDenied, 0, 0, "database.permission_denied" },
        { (int)SqlitePathInspectionStatus.CapabilityNotSupported, 0, 0, "database.provider_validation_failed" },
        { 0, (int)SqliteSidecarInspectionStatus.Present, 0, "database.connection_failed" },
        { 0, (int)SqliteSidecarInspectionStatus.PermissionDenied, 0, "database.permission_denied" },
        { 0, (int)SqliteSidecarInspectionStatus.CapabilityNotSupported, 0, "database.provider_validation_failed" },
        { (int)SqlitePathInspectionStatus.MissingFile, (int)SqliteSidecarInspectionStatus.Present, 0, "database.connection_failed" },
        { (int)SqlitePathInspectionStatus.MissingFile, (int)SqliteSidecarInspectionStatus.PermissionDenied, 0, "database.permission_denied" },
        { (int)SqlitePathInspectionStatus.MissingFile, (int)SqliteSidecarInspectionStatus.CapabilityNotSupported, 0, "database.provider_validation_failed" },
        { 0, 0, (int)SqliteDatabaseInspectionStatus.Connectable, null },
        { 0, 0, (int)SqliteDatabaseInspectionStatus.PermissionDenied, "database.permission_denied" },
        { 0, 0, (int)SqliteDatabaseInspectionStatus.TargetConflict, "database.connection_failed" },
        { 0, 0, (int)SqliteDatabaseInspectionStatus.ConnectionFailed, "database.connection_failed" }
    };

    [Theory]
    [MemberData(nameof(ObservationCases))]
    public async Task Observation_classifications_use_only_fixed_safe_bootstrap_codes(
        int pathStatus, int sidecarStatus, int databaseStatus, string? expectedCode)
    {
        var fileSystem = new ProbeFileSystem
        {
            Status = (SqlitePathInspectionStatus)pathStatus,
            Sidecars = (SqliteSidecarInspectionStatus)sidecarStatus
        };
        var access = new ProbeDatabaseAccess { Status = (SqliteDatabaseInspectionStatus)databaseStatus };
        var target = Target();

        AssertSafeResult(await CreateProvider(fileSystem, access).ValidateAsync(target, Token), target, expectedCode);
        Assert.Equal(pathStatus == 0 && sidecarStatus == 0 ? 1 : 0, access.InspectCalls);
        Assert.Equal(0, fileSystem.WriteCalls);
        Assert.Equal(0, access.InitializeCalls);
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("sidecar")]
    [InlineData("database")]
    [InlineData("internal-cancellation")]
    [InlineData("caller-cancellation")]
    [InlineData("pre-cancellation")]
    public async Task Exceptions_are_sanitized_and_only_caller_cancellation_propagates(string scenario)
    {
        using var caller = new CancellationTokenSource();
        var target = Target();
        var sensitive = target.ConnectionString + ";Password=exception-secret";
        var fileSystem = new ProbeFileSystem();
        var access = new ProbeDatabaseAccess();
        if (scenario == "filesystem") fileSystem.Failure = new IOException(sensitive);
        if (scenario == "sidecar") fileSystem.SidecarFailure = new IOException(sensitive);
        if (scenario == "database") access.Failure = new InvalidOperationException(sensitive);
        if (scenario == "internal-cancellation") access.Failure = new OperationCanceledException(sensitive);
        if (scenario == "caller-cancellation")
        {
            access.BeforeInspect = caller.Cancel;
            access.Failure = new IOException(sensitive);
        }
        if (scenario == "pre-cancellation") caller.Cancel();

        var provider = CreateProvider(fileSystem, access);
        if (scenario is "caller-cancellation" or "pre-cancellation")
        {
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provider.ValidateAsync(target, caller.Token).AsTask());
            Assert.Equal(caller.Token, exception.CancellationToken);
            Assert.Null(exception.InnerException);
            Assert.DoesNotContain(PrivatePath, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("exception-secret", exception.ToString(), StringComparison.Ordinal);
            if (scenario == "pre-cancellation") Assert.Equal(0, fileSystem.InspectCalls);
        }
        else
        {
            AssertSafeResult(await provider.ValidateAsync(target, caller.Token), target,
                "database.provider_validation_failed");
        }
        Assert.Equal(0, fileSystem.WriteCalls);
        Assert.Equal(0, access.InitializeCalls);
    }

    [Fact]
    public async Task Manager_persists_canonical_SQLite_only_after_explicit_preparation()
    {
        var temporaryRoot = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            temporaryRoot = "/private" + temporaryRoot;
        }
        var directory = Directory.CreateDirectory(Path.Combine(temporaryRoot, "sqlite-bootstrap-" + Guid.NewGuid().ToString("N")));
        try
        {
            var databasePath = Path.Combine(directory.FullName, "target.db");
            var target = new BootstrapDatabaseConfiguration("sQlItE", null,
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
            var registry = new BootstrapDatabaseProviderRegistry([new SqliteBootstrapDatabaseProvider()]);
            var store = new BootstrapFileStore(Service, registry, Path.Combine(directory.FullName, "bootstrap.json"));
            var manager = new BootstrapConfigurationManager(store, Instance, new BootstrapDatabaseCandidateValidator(registry));

            var failure = await Assert.ThrowsAsync<BootstrapManagementException>(() =>
                manager.CreateAsync(new BootstrapCreateRequest(target, "master-key"), Token).AsTask());
            Assert.Equal("database.target_not_found", failure.ErrorCode);
            Assert.Empty(directory.EnumerateFileSystemInfos());

            var prepared = await new SqliteDatabaseTargetPreparationProvider().PrepareAsync(
                DatabaseTargetPreparationRequest.ForFile(target), TimeSpan.FromSeconds(5), Token);
            Assert.True(prepared.Succeeded);
            var bytes = await File.ReadAllBytesAsync(databasePath, Token);
            await manager.CreateAsync(new BootstrapCreateRequest(target, "master-key"), Token);
            await manager.UpdateAsync(new BootstrapUpdateRequest(replacementMasterKey: "replacement-key"), Token);

            Assert.Equal("SQLite", store.Load().Database.Provider);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, Token));
            Assert.Equal("SQLite", persisted.RootElement.GetProperty("Database").GetProperty("Provider").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(databasePath, Token));
            Assert.Equal(2, directory.EnumerateFileSystemInfos().Count());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static BootstrapDatabaseConfiguration Target() => new("sQlItE", null,
        new SqliteConnectionStringBuilder { DataSource = PrivatePath, Password = "" }.ConnectionString);

    private static SqliteBootstrapDatabaseProvider CreateProvider(ProbeFileSystem fileSystem, ProbeDatabaseAccess access) =>
        new(new SqliteDatabaseTargetPreparationProvider(fileSystem, access));

    private static void AssertSafeResult(BootstrapValidationResult result,
        BootstrapDatabaseConfiguration target, string? expectedCode)
    {
        Assert.Equal(expectedCode is null, result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
        var output = result + JsonSerializer.Serialize(result);
        Assert.DoesNotContain(PrivatePath, output, StringComparison.Ordinal);
        Assert.DoesNotContain(target.ConnectionString, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception-secret", output, StringComparison.Ordinal);
    }

    private sealed class ProbeFileSystem : ISqliteTargetFileSystem
    {
        internal SqlitePathInspectionStatus Status;
        internal SqliteSidecarInspectionStatus Sidecars;
        internal Exception? Failure;
        internal Exception? SidecarFailure;
        internal int InspectCalls;
        internal int WriteCalls;
        public SqlitePathInspection Inspect(string path)
        {
            InspectCalls++;
            if (Failure is not null) throw Failure;
            return new(Status, path);
        }
        public SqliteSidecarInspectionStatus InspectSidecars(string canonicalPath) =>
            SidecarFailure is null ? Sidecars : throw SidecarFailure;
        public string CreateTemporaryFile(string canonicalTargetPath) { WriteCalls++; throw new InvalidOperationException(); }
        public SqlitePublishStatus Publish(string temporaryPath, string canonicalTargetPath) { WriteCalls++; throw new InvalidOperationException(); }
        public void DeleteTemporaryFile(string temporaryPath) { WriteCalls++; throw new InvalidOperationException(); }
    }

    private sealed class ProbeDatabaseAccess : ISqliteDatabaseAccess
    {
        internal SqliteDatabaseInspectionStatus Status;
        internal Exception? Failure;
        internal Action? BeforeInspect;
        internal int InspectCalls;
        internal int InitializeCalls;
        public ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(string canonicalPath, CancellationToken cancellationToken)
        {
            InspectCalls++;
            BeforeInspect?.Invoke();
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(Status);
        }
        public ValueTask InitializeAsync(string temporaryPath, CancellationToken cancellationToken)
        {
            InitializeCalls++;
            throw new InvalidOperationException();
        }
    }
}

using System;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class BootstrapProviderArchitectureTests
{
    [Fact]
    public void Registry_resolves_provider_by_canonical_id()
    {
        var postgres = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.PostgreSql,
                "PostgreSQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional));
        var sqlite = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.Sqlite,
                "SQLite",
                BootstrapDatabaseTargetKind.File,
                BootstrapServerVersionRequirement.Optional));
        var registry = new BootstrapDatabaseProviderRegistry([postgres, sqlite]);

        var found = registry.TryGetProvider(WellKnownDatabaseProviderIds.PostgreSql, out var provider);

        Assert.True(found);
        Assert.NotNull(provider);
        Assert.Equal(WellKnownDatabaseProviderIds.PostgreSql, provider!.Descriptor.Id);
    }

    [Fact]
    public void Registry_resolves_provider_by_alias()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.SqlServer,
                "SQL Server",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional,
                aliases: ["mssql", "mssqlserver"]));
        var registry = new BootstrapDatabaseProviderRegistry([provider]);

        var found = registry.TryGetProvider("MSSQL", out var selected);
        var canonicalFound = registry.TryGetCanonicalProviderId("mssqlserver", out var canonicalProviderId);

        Assert.True(found);
        Assert.NotNull(selected);
        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, selected!.Descriptor.Id);
        Assert.True(canonicalFound);
        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, canonicalProviderId);
    }

    [Fact]
    public void Registry_lookups_are_case_insensitive()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.MySql,
                "MySQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional));
        var registry = new BootstrapDatabaseProviderRegistry([provider]);

        var found = registry.TryGetProvider("mysql", out var selected);

        Assert.True(found);
        Assert.NotNull(selected);
        Assert.Equal(WellKnownDatabaseProviderIds.MySql, selected!.Descriptor.Id);
    }

    [Fact]
    public void Registry_rejects_duplicate_canonical_provider_id()
    {
        var first = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor("PostgreSQL", "First", BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional));
        var second = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor("postgresql", "Second", BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional));

        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseProviderRegistry([first, second]));
    }

    [Fact]
    public void Registry_rejects_alias_conflicts()
    {
        var first = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                "ProviderA",
                "Provider A",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional,
                aliases: ["shared"]));
        var second = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                "ProviderB",
                "Provider B",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional,
                aliases: ["Shared"]));

        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseProviderRegistry([first, second]));
    }

    [Fact]
    public void Registry_rejects_alias_conflicting_with_canonical_id()
    {
        var first = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.Sqlite,
                "SQLite",
                BootstrapDatabaseTargetKind.File,
                BootstrapServerVersionRequirement.Optional,
                aliases: ["FileDb"]));
        var second = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.MySql,
                "MySQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional,
                aliases: [WellKnownDatabaseProviderIds.Sqlite]));

        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseProviderRegistry([first, second]));
    }

    [Fact]
    public void Registry_descriptors_are_sorted_deterministically()
    {
        var providers = new IBootstrapDatabaseProvider[]
        {
            new FakeProvider(
                new BootstrapDatabaseProviderDescriptor(WellKnownDatabaseProviderIds.SqlServer, "SQL Server",
                    BootstrapDatabaseTargetKind.ServerDatabase, BootstrapServerVersionRequirement.Optional)),
            new FakeProvider(
                new BootstrapDatabaseProviderDescriptor(WellKnownDatabaseProviderIds.Oracle, "Oracle",
                    BootstrapDatabaseTargetKind.ServerSchema, BootstrapServerVersionRequirement.Optional)),
            new FakeProvider(
                new BootstrapDatabaseProviderDescriptor(WellKnownDatabaseProviderIds.PostgreSql, "PostgreSQL",
                    BootstrapDatabaseTargetKind.ServerDatabase, BootstrapServerVersionRequirement.Optional)),
            new FakeProvider(
                new BootstrapDatabaseProviderDescriptor(WellKnownDatabaseProviderIds.MySql, "MySQL",
                    BootstrapDatabaseTargetKind.ServerDatabase, BootstrapServerVersionRequirement.Optional)),
        };

        var registry = new BootstrapDatabaseProviderRegistry(providers);

        var ids = registry.Descriptors.Select(item => item.Id).ToArray();
        Assert.Equal([WellKnownDatabaseProviderIds.MySql, WellKnownDatabaseProviderIds.Oracle, WellKnownDatabaseProviderIds.PostgreSql,
            WellKnownDatabaseProviderIds.SqlServer], ids);
    }

    [Fact]
    public void Provider_descriptor_copies_aliases()
    {
        var aliases = new List<string> { "alias-one", "alias-two" };
        var descriptor = new BootstrapDatabaseProviderDescriptor(
            "ProviderA",
            "Provider A",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Optional,
            aliases);

        aliases.Add("alias-three");

        Assert.Equal(["alias-one", "alias-two"], descriptor.Aliases);
    }

    [Fact]
    public void Provider_descriptor_rejects_empty_display_name()
    {
        Assert.Throws<ArgumentException>(() =>
            new BootstrapDatabaseProviderDescriptor("ProviderA", " ", BootstrapDatabaseTargetKind.File,
                BootstrapServerVersionRequirement.Optional));
    }

    [Fact]
    public async Task Candidate_validator_reports_not_registered_when_provider_is_unknown()
    {
        var registry = new BootstrapDatabaseProviderRegistry(Array.Empty<IBootstrapDatabaseProvider>());
        var validator = new BootstrapDatabaseCandidateValidator(registry);
        var candidate = CreateCandidate("PostgreSQL");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_not_registered", result.ErrorCode);
    }

    [Fact]
    public async Task Candidate_validator_rejects_required_server_version_when_missing()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.Oracle,
                "Oracle",
                BootstrapDatabaseTargetKind.ServerSchema,
                BootstrapServerVersionRequirement.Required));
        var validator = CreateValidator([provider]);

        var candidate = CreateCandidate(
            provider.Descriptor.Id,
            databaseServerVersion: null,
            connectionString: "Host=oracle;ServiceName=test");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_required", result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Candidate_validator_uses_registry_snapshot_for_server_version_requirement()
    {
        var snapshotDescriptor = new BootstrapDatabaseProviderDescriptor(
            WellKnownDatabaseProviderIds.SqlServer,
            "SqlServer",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Required);
        var liveDescriptor = new BootstrapDatabaseProviderDescriptor(
            WellKnownDatabaseProviderIds.SqlServer,
            "SqlServer",
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Optional);
        var provider = new DescriptorChangingProvider(snapshotDescriptor, liveDescriptor);
        var registry = new BootstrapDatabaseProviderRegistry([provider]);
        var validator = new BootstrapDatabaseCandidateValidator(registry);

        var candidate = CreateCandidate(
            WellKnownDatabaseProviderIds.SqlServer,
            databaseServerVersion: null,
            connectionString: "Server=.\\SQLEXPRESS;Database=app;Integrated Security=true");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_required", result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Candidate_validator_rejects_forbidden_server_version_when_present()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.Sqlite,
                "SQLite",
                BootstrapDatabaseTargetKind.File,
                BootstrapServerVersionRequirement.Forbidden));
        var validator = CreateValidator([provider]);

        var candidate = CreateCandidate(
            provider.Descriptor.Id,
            "15",
            "Data Source=test.db;Password=sqlite-password");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.server_version_not_allowed", result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Candidate_validator_calls_provider_when_server_version_requirement_allows()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.MySql,
                "MySQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional));
        var validator = CreateValidator([provider]);

        var candidate = CreateCandidate(
            provider.Descriptor.Id,
            "8.0",
            "Host=mysql;Database=app;Password=mysql-password");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Candidate_validator_canonicalizes_alias_before_provider_dispatch()
    {
        BootstrapDatabaseConfiguration? dispatchedDatabase = null;
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.PostgreSql,
                "PostgreSQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Required,
                aliases: ["postgres"]),
            (database, _) =>
            {
                dispatchedDatabase = database;
                return ValueTask.FromResult(BootstrapValidationResult.Success());
            });
        var validator = CreateValidator([provider]);
        var candidate = CreateCandidate("postgres");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(dispatchedDatabase);
        Assert.Equal(WellKnownDatabaseProviderIds.PostgreSql, dispatchedDatabase.Provider);
    }

    [Fact]
    public async Task Candidate_validator_checks_cancellation_token_is_forwarded()
    {
        var expected = new CancellationTokenSource().Token;
        CancellationToken capturedToken = default;
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.MySql,
                "MySQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional),
            (_, token) =>
            {
                capturedToken = token;
                return ValueTask.FromResult(BootstrapValidationResult.Success());
            });
        var validator = CreateValidator([provider]);

        var candidate = CreateCandidate("MySQL");
        var result = await validator.ValidateAsync(candidate, expected);

        Assert.True(result.IsValid);
        Assert.Equal(expected, capturedToken);
    }

    [Fact]
    public async Task Candidate_validator_propagates_operation_canceled()
    {
        var cancellationSource = new CancellationTokenSource();
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.MySql,
                "MySQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional),
            (_, token) => throw new OperationCanceledException(token));
        var validator = CreateValidator([provider]);

        var candidate = CreateCandidate("MySQL");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(candidate, cancellationSource.Token).AsTask());
    }

    [Fact]
    public async Task Candidate_validator_hides_connection_secret_on_provider_exception()
    {
        const string connectionSecret = "Password=my-top-secret";
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.PostgreSql,
                "PostgreSQL",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional),
            (_, _) => throw new InvalidOperationException(
                $"ConnectionString contains {connectionSecret}"));
        var validator = CreateValidator([provider]);
        var candidate = CreateCandidate(
            WellKnownDatabaseProviderIds.PostgreSql,
            connectionString: $"Host=db;{connectionSecret}");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_validation_failed", result.ErrorCode);
        Assert.DoesNotContain(connectionSecret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_validator_treats_null_provider_result_as_failure()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.Oracle,
                "Oracle",
                BootstrapDatabaseTargetKind.ServerSchema,
                BootstrapServerVersionRequirement.Optional),
            (_, _) => new ValueTask<BootstrapValidationResult>(Task.FromResult<BootstrapValidationResult>(null!)));
        var validator = CreateValidator([provider]);
        var candidate = CreateCandidate(WellKnownDatabaseProviderIds.Oracle);

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_invalid_result", result.ErrorCode);
    }

    [Fact]
    public async Task Candidate_validator_passes_safe_failure_code_from_provider()
    {
        var provider = new FakeProvider(
            new BootstrapDatabaseProviderDescriptor(
                WellKnownDatabaseProviderIds.SqlServer,
                "Sql Server",
                BootstrapDatabaseTargetKind.ServerDatabase,
                BootstrapServerVersionRequirement.Optional),
            (_, _) => ValueTask.FromResult(BootstrapValidationResult.Failure("database.provider_rejected")));
        var validator = CreateValidator([provider]);
        var candidate = CreateCandidate(WellKnownDatabaseProviderIds.SqlServer);

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("database.provider_rejected", result.ErrorCode);
    }

    [Fact]
    public void Candidate_validator_does_not_receive_bootstrap_master_key()
    {
        var method = typeof(IBootstrapDatabaseProvider).GetMethod(nameof(IBootstrapDatabaseProvider.ValidateAsync));
        Assert.NotNull(method);

        var parameterType = method!.GetParameters()[0].ParameterType;
        Assert.Equal(typeof(BootstrapDatabaseConfiguration), parameterType);
    }

    [Fact]
    public void Well_known_provider_ids_are_stable()
    {
        Assert.Equal("PostgreSQL", WellKnownDatabaseProviderIds.PostgreSql);
        Assert.Equal("SQLite", WellKnownDatabaseProviderIds.Sqlite);
        Assert.Equal("MySQL", WellKnownDatabaseProviderIds.MySql);
        Assert.Equal("MariaDB", WellKnownDatabaseProviderIds.MariaDb);
        Assert.Equal("Oracle", WellKnownDatabaseProviderIds.Oracle);
        Assert.Equal("SqlServer", WellKnownDatabaseProviderIds.SqlServer);
    }

    [Fact]
    public void Descriptor_rejects_undefined_target_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapDatabaseProviderDescriptor(
                "UndefinedKind",
                "Undefined Kind Provider",
                (BootstrapDatabaseTargetKind)99,
                BootstrapServerVersionRequirement.Optional));
    }

    [Fact]
    public void Descriptor_rejects_undefined_server_version_requirement()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapDatabaseProviderDescriptor(
                "UndefinedRequirement",
                "Undefined Requirement Provider",
                BootstrapDatabaseTargetKind.File,
                (BootstrapServerVersionRequirement)99));
    }

    private static BootstrapDatabaseCandidateValidator CreateValidator(
        params IBootstrapDatabaseProvider[] providers) =>
        new(new BootstrapDatabaseProviderRegistry(providers));

    private static BootstrapConfiguration CreateCandidate(
        string provider,
        string? databaseServerVersion = "16",
        string connectionString = "Host=db;Password=validator-password")
    {
        return new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(
                provider,
                databaseServerVersion,
                connectionString),
            "validator-master-key");
    }

    private sealed class FakeProvider : IBootstrapDatabaseProvider
    {
        private readonly Func<BootstrapDatabaseConfiguration, CancellationToken, ValueTask<BootstrapValidationResult>>? handler;

        public FakeProvider(
            BootstrapDatabaseProviderDescriptor descriptor,
            Func<BootstrapDatabaseConfiguration, CancellationToken, ValueTask<BootstrapValidationResult>>? handler = null)
        {
            Descriptor = descriptor;
            this.handler = handler;
        }

        public BootstrapDatabaseProviderDescriptor Descriptor { get; }

        public int CallCount { get; private set; }

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapDatabaseConfiguration database,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (handler is null)
            {
                return ValueTask.FromResult(BootstrapValidationResult.Success());
            }

            return handler(database, cancellationToken);
        }
    }

    private sealed class DescriptorChangingProvider : IBootstrapDatabaseProvider
    {
        private readonly BootstrapDatabaseProviderDescriptor initialDescriptor;
        private readonly BootstrapDatabaseProviderDescriptor currentDescriptor;
        private bool accessed;

        public DescriptorChangingProvider(
            BootstrapDatabaseProviderDescriptor initialDescriptor,
            BootstrapDatabaseProviderDescriptor currentDescriptor)
        {
            this.initialDescriptor = initialDescriptor;
            this.currentDescriptor = currentDescriptor;
        }

        public BootstrapDatabaseProviderDescriptor Descriptor => accessed ? currentDescriptor : GetAndLock();
        public int CallCount { get; private set; }

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapDatabaseConfiguration database,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(BootstrapValidationResult.Success());
        }

        private BootstrapDatabaseProviderDescriptor GetAndLock()
        {
            accessed = true;
            return initialDescriptor;
        }
    }
}

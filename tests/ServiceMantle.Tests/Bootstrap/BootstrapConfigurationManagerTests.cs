using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class BootstrapConfigurationManagerTests
{
    [Fact]
    public void GetStatus_returns_unconfigured_when_file_is_missing()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var instanceId = InstanceId.Parse("Node-A3");
        var store = CreateStore(directory, serviceId);
        var manager = CreateManager(store, instanceId);

        var status = manager.GetStatus();

        Assert.Equal(serviceId, status.ServiceId);
        Assert.Equal(instanceId, status.InstanceId);
        Assert.False(status.IsConfigured);
        Assert.Null(status.Provider);
        Assert.Null(status.ServerVersion);
        Assert.False(status.ConnectionStringConfigured);
        Assert.False(status.MasterKeyConfigured);
    }

    [Fact]
    public void GetStatus_returns_only_safe_configured_fields()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        const string connectionSecret = "Host=db;Database=signacore;Password=status-password";
        const string masterSecret = "status-master-key";
        store.Create(CreateConfiguration(connectionString: connectionSecret, masterKey: masterSecret));
        var manager = CreateManager(store);

        var status = manager.GetStatus();
        var serialized = JsonSerializer.Serialize(status);
        var text = status.ToString();

        Assert.True(status.IsConfigured);
        Assert.Equal("PostgreSQL", status.Provider);
        Assert.Equal("15", status.ServerVersion);
        Assert.True(status.ConnectionStringConfigured);
        Assert.True(status.MasterKeyConfigured);
        Assert.DoesNotContain(connectionSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(masterSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(masterSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapConfiguration", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_propagates_a_corrupt_existing_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, "{\"Database\":");
        var manager = CreateManager(store);

        Assert.Throws<BootstrapException>(() => manager.GetStatus());
    }

    [Fact]
    public void GetStatus_propagates_a_service_id_mismatch()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "ServiceId": "other-service",
              "Database": {
                "Provider": "PostgreSQL",
                "ConnectionString": "Host=db;Password=mismatch-password"
              },
              "MasterKey": "mismatch-master-key"
            }
            """);
        var manager = CreateManager(store);

        Assert.Throws<BootstrapException>(() => manager.GetStatus());
    }

    [Fact]
    public async Task CreateAsync_validates_then_creates_the_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new FakeValidator();
        var manager = CreateManager(store, validator: validator);
        var request = new BootstrapCreateRequest(
            new BootstrapDatabaseConfiguration("PostgreSQL", "15", "Host=db;Password=create-password"),
            "create-master-key");

        var result = await manager.CreateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, validator.CallCount);
        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(BootstrapChangeOperation.Create, result.Operation);
        Assert.True(result.RestartRequired);
        Assert.Equal(store.ServiceId, result.ServiceId);
        Assert.Equal("Node-A3", result.InstanceId.Value);
    }

    [Fact]
    public async Task CreateAsync_does_not_create_when_validation_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new FakeValidator
        {
            Result = BootstrapValidationResult.Failure("database.unavailable")
        };
        var manager = CreateManager(store, validator: validator);

        var exception = await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.CreateAsync(CreateRequest(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("database.unavailable", exception.ErrorCode);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task CreateAsync_does_not_overwrite_an_existing_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(connectionString: "Host=db;Password=original-password"));
        var original = File.ReadAllBytes(store.FilePath);
        var manager = CreateManager(store);

        await Assert.ThrowsAsync<BootstrapException>(() =>
            manager.CreateAsync(
                CreateRequest("Host=db;Password=new-password"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public async Task UpdateAsync_without_master_key_replacement_preserves_the_existing_master_key()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(
            connectionString: "Host=db;Database=original;Password=original-password",
            masterKey: "original-master-key"));
        var manager = CreateManager(store);

        var result = await manager.UpdateAsync(
            new BootstrapUpdateRequest(
                new BootstrapDatabaseConfiguration(
                    "SQLite",
                    null,
                    "Data Source=updated.db;Password=updated-password")),
            TestContext.Current.CancellationToken);
        var configuration = store.Load();

        Assert.Equal("SQLite", configuration.Database.Provider);
        Assert.Equal("Data Source=updated.db;Password=updated-password", configuration.Database.ConnectionString);
        Assert.Equal("original-master-key", configuration.MasterKey);
        Assert.Equal(BootstrapChangeOperation.Update, result.Operation);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task UpdateAsync_replacing_only_the_master_key_preserves_the_database_configuration()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var original = CreateConfiguration(
            connectionString: "Host=db;Database=original;Password=original-password",
            masterKey: "original-master-key");
        store.Create(original);
        var manager = CreateManager(store);

        await manager.UpdateAsync(
            new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
            TestContext.Current.CancellationToken);
        var configuration = store.Load();

        Assert.Equal(original.Database.Provider, configuration.Database.Provider);
        Assert.Equal(original.Database.ServerVersion, configuration.Database.ServerVersion);
        Assert.Equal(original.Database.ConnectionString, configuration.Database.ConnectionString);
        Assert.Equal("replacement-master-key", configuration.MasterKey);
    }

    [Fact]
    public async Task UpdateAsync_can_replace_the_database_without_receiving_the_old_connection_string()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        const string originalConnection = "Host=db;Database=original;Password=original-password";
        store.Create(CreateConfiguration(connectionString: originalConnection, masterKey: "original-master-key"));
        var manager = CreateManager(store);
        var request = new BootstrapUpdateRequest(
            new BootstrapDatabaseConfiguration("PostgreSQL", "16", "Host=new-db;Password=new-password"));

        var result = await manager.UpdateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Host=new-db;Password=new-password", store.Load().Database.ConnectionString);
        Assert.DoesNotContain(originalConnection, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(originalConnection, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_empty_update()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new FakeValidator();
        var manager = CreateManager(store, validator: validator);

        var exception = await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("update.empty", exception.ErrorCode);
        Assert.Equal(0, validator.CallCount);
    }

    [Fact]
    public async Task UpdateAsync_keeps_the_original_bytes_when_validation_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(connectionString: "Host=db;Password=original-password"));
        var original = File.ReadAllBytes(store.FilePath);
        var validator = new FakeValidator
        {
            Result = BootstrapValidationResult.Failure("candidate.rejected")
        };
        var manager = CreateManager(store, validator: validator);

        await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public async Task UpdateAsync_keeps_the_original_bytes_when_cancelled()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(connectionString: "Host=db;Password=original-password"));
        var original = File.ReadAllBytes(store.FilePath);
        var validator = new FakeValidator
        {
            Handler = (_, token) => throw new OperationCanceledException(token)
        };
        var manager = CreateManager(store, validator: validator);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public async Task Validator_exceptions_are_wrapped_without_secret_values()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(connectionString: "Host=db;Password=original-password"));
        const string connectionSecret = "Host=new-db;Password=validator-password";
        const string masterSecret = "validator-master-key";
        var validator = new FakeValidator
        {
            Handler = (candidate, _) => throw new InvalidOperationException(
                $"ConnectionString={candidate.Database.ConnectionString}; MasterKey={candidate.MasterKey}")
        };
        var manager = CreateManager(store, validator: validator);

        var exception = await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(
                    new BootstrapDatabaseConfiguration("PostgreSQL", null, connectionSecret),
                    masterSecret),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("candidate.validation_failed", exception.ErrorCode);
        Assert.DoesNotContain(connectionSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(masterSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(masterSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Two_modifications_on_one_manager_do_not_validate_concurrently()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration());
        var validator = new SerializingValidator();
        var manager = CreateManager(store, validator: validator);

        var first = manager.UpdateAsync(
            new BootstrapUpdateRequest(replacementMasterKey: "first-master-key"),
            TestContext.Current.CancellationToken).AsTask();
        await validator.FirstValidationStarted.Task;

        var second = manager.UpdateAsync(
            new BootstrapUpdateRequest(replacementMasterKey: "second-master-key"),
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(second.IsCompleted);
        validator.ReleaseFirstValidation.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, validator.MaximumConcurrentValidations);
    }

    private static BootstrapConfigurationManager CreateManager(
        BootstrapFileStore store,
        InstanceId? instanceId = null,
        IBootstrapCandidateValidator? validator = null) =>
        new(
            store,
            instanceId ?? InstanceId.Parse("Node-A3"),
            validator ?? new FakeValidator());

    private static BootstrapFileStore CreateStore(
        TemporaryDirectory directory,
        ServiceId? serviceId = null)
    {
        var actualServiceId = serviceId ?? ServiceId.Parse("signacore");
        var filePath = Path.Combine(directory.Path, "config", "signacore.bootstrap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return new BootstrapFileStore(actualServiceId, filePath);
    }

    private static BootstrapCreateRequest CreateRequest(
        string connectionString = "Host=db;Password=create-password") =>
        new(
            new BootstrapDatabaseConfiguration("PostgreSQL", "15", connectionString),
            "create-master-key");

    private static BootstrapConfiguration CreateConfiguration(
        ServiceId? serviceId = null,
        string connectionString = "Host=db;Database=signacore;Password=test-password",
        string masterKey = "test-master-key")
    {
        var actualServiceId = serviceId ?? ServiceId.Parse("signacore");
        return new BootstrapConfiguration(
            actualServiceId,
            new BootstrapDatabaseConfiguration("PostgreSQL", "15", connectionString),
            masterKey);
    }

    private sealed class FakeValidator : IBootstrapCandidateValidator
    {
        public BootstrapValidationResult Result { get; set; } = BootstrapValidationResult.Success();

        public Func<BootstrapConfiguration, CancellationToken, ValueTask<BootstrapValidationResult>>? Handler { get; set; }

        public int CallCount { get; private set; }

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Handler is not null)
            {
                return Handler(candidate, cancellationToken);
            }

            return ValueTask.FromResult(Result);
        }
    }

    private sealed class SerializingValidator : IBootstrapCandidateValidator
    {
        private int activeValidations;
        private int validationCount;

        public TaskCompletionSource FirstValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstValidation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentValidations => maximumConcurrentValidations;

        public async ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeValidations);
            UpdateMaximum(active);
            var currentValidation = Interlocked.Increment(ref validationCount);

            try
            {
                if (currentValidation == 1)
                {
                    FirstValidationStarted.TrySetResult();
                    await ReleaseFirstValidation.Task.WaitAsync(cancellationToken);
                }

                return BootstrapValidationResult.Success();
            }
            finally
            {
                Interlocked.Decrement(ref activeValidations);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var currentMaximum = Volatile.Read(ref maximumConcurrentValidations);
                if (active <= currentMaximum ||
                    Interlocked.CompareExchange(ref maximumConcurrentValidations, active, currentMaximum) == currentMaximum)
                {
                    return;
                }
            }
        }

        private int maximumConcurrentValidations;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ServiceMantle.Tests",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

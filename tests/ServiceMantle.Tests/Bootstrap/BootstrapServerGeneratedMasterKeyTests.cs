using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the server-generated master key on creation: the generated value follows the same
/// validation and write path as a supplied one, never leaves the Bootstrap file, is discarded on
/// every failure, and leaves the file format untouched.
/// </summary>
public sealed class BootstrapServerGeneratedMasterKeyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_generates_a_base64url_key_and_writes_it_to_the_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, ServiceId.Parse("first"));
        var validator = new RecordingValidator();
        var manager = new BootstrapConfigurationManager(store, InstanceId.Parse("Node-A3"), validator);

        var result = await manager.CreateAsync(
            new BootstrapCreateRequest(CreateDatabase("Host=db;Password=p")),
            Token);

        var masterKey = store.Load().MasterKey;
        Assert.Equal(BootstrapChangeOperation.Create, result.Operation);
        Assert.NotNull(masterKey);
        // Invariant 4: 256 bits of entropy rendered as 43 unpadded Base64URL characters.
        Assert.Equal(43, masterKey.Length);
        Assert.DoesNotContain('=', masterKey);
        Assert.All(masterKey, character => Assert.True(
            character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'));
        // The generated value went through the same candidate validation as a supplied one.
        Assert.Equal(1, validator.Calls);
        Assert.Equal(masterKey, validator.LastMasterKey);
        // The result projection never carries the key.
        Assert.DoesNotContain(masterKey, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_independent_creations_generate_two_different_keys()
    {
        using var directory = TemporaryDirectory.Create();
        var first = new BootstrapConfigurationManager(
            CreateStore(directory, ServiceId.Parse("first")),
            InstanceId.Parse("Node-A3"),
            new RecordingValidator());
        var second = new BootstrapConfigurationManager(
            CreateStore(directory, ServiceId.Parse("second")),
            InstanceId.Parse("Node-A3"),
            new RecordingValidator());

        await first.CreateAsync(new BootstrapCreateRequest(CreateDatabase()), Token);
        await second.CreateAsync(new BootstrapCreateRequest(CreateDatabase()), Token);

        var firstKey = LoadMasterKey(directory, "first");
        var secondKey = LoadMasterKey(directory, "second");
        Assert.NotEqual(firstKey, secondKey);
        Assert.All(new[] { firstKey, secondKey }, key => Assert.Equal(43, key!.Length));
    }

    [Fact]
    public async Task A_generated_key_rejected_by_the_validator_leaves_no_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new RecordingValidator
        {
            Result = BootstrapValidationResult.Failure("database.unavailable")
        };
        var manager = new BootstrapConfigurationManager(store, InstanceId.Parse("Node-A3"), validator);

        var exception = await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.CreateAsync(
                new BootstrapCreateRequest(CreateDatabase()),
                Token).AsTask());

        Assert.Equal("database.unavailable", exception.ErrorCode);
        Assert.Equal(1, validator.Calls);
        Assert.NotNull(validator.LastMasterKey);
        // The discarded candidate never reaches a message or a projection.
        Assert.DoesNotContain(validator.LastMasterKey!, exception.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task A_write_failure_leaves_no_file_and_discards_the_generated_key()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(new BootstrapConfiguration(
            store.ServiceId,
            CreateDatabase("Host=db;Password=original"),
            "original-master-key"));
        var original = await File.ReadAllBytesAsync(store.FilePath, Token);
        var validator = new RecordingValidator();
        var manager = new BootstrapConfigurationManager(store, InstanceId.Parse("Node-A3"), validator);

        await Assert.ThrowsAsync<BootstrapException>(() =>
            manager.CreateAsync(
                new BootstrapCreateRequest(CreateDatabase()),
                Token).AsTask());

        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, Token));
    }

    [Fact]
    public async Task A_cancelled_caller_never_validates_or_writes_anything()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new RecordingValidator();
        var manager = new BootstrapConfigurationManager(store, InstanceId.Parse("Node-A3"), validator);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        // Reverse validation of the boundary checkpoint: without the entry check the operation
        // would proceed to validation and the file write, so both counters would move.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.CreateAsync(
                new BootstrapCreateRequest(CreateDatabase()),
                abort.Token).AsTask());

        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Equal(0, validator.Calls);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task The_bootstrap_file_format_is_unchanged_by_a_generated_key()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        var validator = new RecordingValidator();
        var manager = new BootstrapConfigurationManager(store, InstanceId.Parse("Node-A3"), validator);

        await manager.CreateAsync(new BootstrapCreateRequest(CreateDatabase()), Token);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, Token));
        Assert.Equal(1, document.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(
            ["FormatVersion", "ServiceId", "Database", "MasterKey"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static BootstrapDatabaseConfiguration CreateDatabase(
        string connectionString = "Host=db;Password=p") =>
        new("PostgreSQL", "15", connectionString);

    private static BootstrapFileStore CreateStore(
        TemporaryDirectory directory,
        ServiceId? serviceId = null)
    {
        var actualServiceId = serviceId ?? ServiceId.Parse("signacore");
        var filePath = Path.Combine(directory.Path, "config", $"{actualServiceId.Value}.bootstrap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return new BootstrapFileStore(
            actualServiceId,
            new BootstrapDatabaseProviderRegistry([]),
            filePath);
    }

    private static string? LoadMasterKey(TemporaryDirectory directory, string serviceId) =>
        new BootstrapFileStore(
            ServiceId.Parse(serviceId),
            new BootstrapDatabaseProviderRegistry([]),
            Path.Combine(directory.Path, "config", $"{serviceId}.bootstrap.json")).Load().MasterKey;

    private sealed class RecordingValidator : IBootstrapCandidateValidator
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal string? LastMasterKey { get; private set; }

        internal BootstrapValidationResult Result { get; set; } = BootstrapValidationResult.Success();

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            LastMasterKey = candidate.MasterKey;
            return ValueTask.FromResult(Result);
        }
    }
}

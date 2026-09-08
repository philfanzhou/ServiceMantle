using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Pins the closed machine-readable classification a consumer may branch on, over the same input
/// matrix reached directly through the store and through the manager use cases.
/// </summary>
public sealed class BootstrapFileFailureKindTests
{
    private const string ConnectionSecret = "Host=db;Database=signacore;Password=classification-password";
    private const string MasterSecret = "classification-master-key";

    [Fact]
    public void The_failure_kind_set_is_closed_and_defaults_to_Unavailable()
    {
        Assert.Equal(
            new[]
            {
                BootstrapFileFailureKind.Unavailable,
                BootstrapFileFailureKind.TargetAlreadyExists,
                BootstrapFileFailureKind.TargetMissing
            },
            Enum.GetValues<BootstrapFileFailureKind>());

        Assert.Equal(0, (int)BootstrapFileFailureKind.Unavailable);
        Assert.Equal(1, (int)BootstrapFileFailureKind.TargetAlreadyExists);
        Assert.Equal(2, (int)BootstrapFileFailureKind.TargetMissing);
        Assert.Equal(BootstrapFileFailureKind.Unavailable, default(BootstrapFileFailureKind));
    }

    [Fact]
    public void Store_classifies_the_failure_matrix()
    {
        using var directory = TemporaryDirectory.Create();

        // Missing target: load and replace.
        var missingStore = CreateStore(directory, "missing");
        Assert.Null(missingStore.TryLoad());
        Assert.Equal(
            BootstrapFileFailureKind.TargetMissing,
            Assert.Throws<BootstrapException>(() => missingStore.Load()).FailureKind);
        Assert.Equal(
            BootstrapFileFailureKind.TargetMissing,
            Assert.Throws<BootstrapException>(
                () => missingStore.Replace(CreateConfiguration())).FailureKind);

        // Legal create, then legal replace: no failure at all.
        var writableStore = CreateStore(directory, "writable");
        writableStore.Create(CreateConfiguration());
        writableStore.Replace(CreateConfiguration(connectionString: "Host=db;Password=replaced"));
        Assert.Equal("Host=db;Password=replaced", writableStore.Load().Database.ConnectionString);

        // Existing target: create refuses to overwrite.
        Assert.Equal(
            BootstrapFileFailureKind.TargetAlreadyExists,
            Assert.Throws<BootstrapException>(
                () => writableStore.Create(CreateConfiguration())).FailureKind);

        // Corrupt JSON is not an absence.
        var corruptStore = CreateStore(directory, "corrupt");
        File.WriteAllText(corruptStore.FilePath, "{ \"Database\": ");
        Assert.Equal(
            BootstrapFileFailureKind.Unavailable,
            Assert.Throws<BootstrapException>(() => corruptStore.Load()).FailureKind);
        Assert.Equal(
            BootstrapFileFailureKind.Unavailable,
            Assert.Throws<BootstrapException>(() => corruptStore.TryLoad()).FailureKind);

        // A file that belongs to another service is not an absence either.
        var mismatchedStore = CreateStore(directory, "mismatched");
        CreateStore(directory, "mismatched", ServiceId.Parse("other-service"))
            .Create(CreateConfiguration(ServiceId.Parse("other-service")));
        Assert.Equal(
            BootstrapFileFailureKind.Unavailable,
            Assert.Throws<BootstrapException>(() => mismatchedStore.Load()).FailureKind);

        // Writing for a different service is a refusal, not a statement about the target.
        Assert.Equal(
            BootstrapFileFailureKind.Unavailable,
            Assert.Throws<BootstrapException>(
                () => writableStore.Create(
                    CreateConfiguration(ServiceId.Parse("other-service")))).FailureKind);
    }

    [Fact]
    public async Task Manager_use_cases_report_the_same_classification()
    {
        using var directory = TemporaryDirectory.Create();

        // Missing target through UpdateAsync.
        var missingStore = CreateStore(directory, "missing");
        var missingManager = CreateManager(missingStore);
        var missing = await Assert.ThrowsAsync<BootstrapException>(() =>
            missingManager.UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(BootstrapFileFailureKind.TargetMissing, missing.FailureKind);

        // Legal create, then legal update.
        var store = CreateStore(directory, "writable");
        var manager = CreateManager(store);
        await manager.CreateAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await manager.UpdateAsync(
            new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
            TestContext.Current.CancellationToken);
        Assert.Equal("replacement-master-key", store.Load().MasterKey);

        // Existing target through CreateAsync.
        var existing = await Assert.ThrowsAsync<BootstrapException>(() =>
            manager.CreateAsync(CreateRequest(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(BootstrapFileFailureKind.TargetAlreadyExists, existing.FailureKind);

        // Corrupt JSON through UpdateAsync.
        var corruptStore = CreateStore(directory, "corrupt");
        File.WriteAllText(corruptStore.FilePath, "{ \"Database\": ");
        var corrupt = await Assert.ThrowsAsync<BootstrapException>(() =>
            CreateManager(corruptStore).UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(BootstrapFileFailureKind.Unavailable, corrupt.FailureKind);

        // A file belonging to another service through UpdateAsync.
        var mismatchedStore = CreateStore(directory, "mismatched");
        CreateStore(directory, "mismatched", ServiceId.Parse("other-service"))
            .Create(CreateConfiguration(ServiceId.Parse("other-service")));
        var mismatched = await Assert.ThrowsAsync<BootstrapException>(() =>
            CreateManager(mismatchedStore).UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement-master-key"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(BootstrapFileFailureKind.Unavailable, mismatched.FailureKind);
    }

    [Fact]
    public async Task Concurrent_creates_publish_one_intact_file_and_only_declared_failure_kinds()
    {
        using var directory = TemporaryDirectory.Create();
        const int writers = 8;
        using var start = new Barrier(writers);
        var stores = Enumerable.Range(0, writers)
            .Select(_ => CreateStore(directory, "contended"))
            .ToArray();

        var attempts = stores.Select((store, index) => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                store.Create(CreateConfiguration(connectionString: $"Host=db;Password=writer-{index}"));
                return (Index: index, Failure: (BootstrapException?)null);
            }
            catch (BootstrapException exception)
            {
                return (Index: index, Failure: exception);
            }
        })).ToArray();

        var results = await Task.WhenAll(attempts);

        var winners = results.Where(result => result.Failure is null).ToArray();
        Assert.Single(winners);

        var losers = results.Where(result => result.Failure is not null).ToArray();
        foreach (var loser in losers)
        {
            // Losing the non-overwriting publish is only classified TargetAlreadyExists when the
            // store actually observed the target; a race it could not prove stays Unavailable.
            Assert.Contains(
                loser.Failure!.FailureKind,
                new[] { BootstrapFileFailureKind.TargetAlreadyExists, BootstrapFileFailureKind.Unavailable });
        }

        // The exclusive reservation gives every loser positive evidence of the conflict.
        Assert.Contains(losers, loser => loser.Failure!.FailureKind == BootstrapFileFailureKind.TargetAlreadyExists);

        var published = stores[0].Load();
        Assert.Equal($"Host=db;Password=writer-{winners[0].Index}", published.Database.ConnectionString);
        Assert.Equal(MasterSecret, published.MasterKey);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stores[0].FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_creates_never_overwrite_a_file_that_is_already_there()
    {
        using var directory = TemporaryDirectory.Create();
        const int writers = 8;
        var owner = CreateStore(directory, "occupied");
        owner.Create(CreateConfiguration(connectionString: "Host=db;Password=incumbent"));
        var original = File.ReadAllBytes(owner.FilePath);

        using var start = new Barrier(writers);
        var attempts = Enumerable.Range(0, writers).Select(index =>
        {
            var store = CreateStore(directory, "occupied");
            return Task.Run(() =>
            {
                start.SignalAndWait();
                return Assert.Throws<BootstrapException>(
                    () => store.Create(CreateConfiguration(connectionString: $"Host=db;Password=writer-{index}")));
            });
        }).ToArray();

        var failures = await Task.WhenAll(attempts);

        Assert.All(failures, failure =>
            Assert.Equal(BootstrapFileFailureKind.TargetAlreadyExists, failure.FailureKind));
        Assert.Equal(original, File.ReadAllBytes(owner.FilePath));
        Assert.Equal("Host=db;Password=incumbent", owner.Load().Database.ConnectionString);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(owner.FilePath)!, "*.tmp"));
    }

    [Fact]
    public void A_refused_create_leaves_no_target_behind()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, "refused");

        // Refused before anything is reserved, so the path must still be free afterwards.
        Assert.Throws<BootstrapException>(
            () => store.Create(CreateConfiguration(ServiceId.Parse("other-service"))));

        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.TryLoad());
        store.Create(CreateConfiguration());
        Assert.Equal(ConnectionSecret, store.Load().Database.ConnectionString);
    }

    [Fact]
    public void A_create_that_fails_after_reserving_releases_the_reservation()
    {
        using var directory = TemporaryDirectory.Create();
        var configurationDirectory = Path.Combine(directory.Path, "abandoned");
        Directory.CreateDirectory(configurationDirectory);

        // The content is written beside the target as ".{file name}.{12 random characters}.tmp", so
        // a target name that fits the file system's component limit while that longer name does not
        // fails the create after the reservation was already taken.
        const string suffix = ".bootstrap.json";
        var fileName = new string('n', 245 - suffix.Length) + suffix;
        var temporaryName = $".{fileName}.{Path.GetRandomFileName()}.tmp";

        if (!NameIsAccepted(configurationDirectory, fileName) ||
            NameIsAccepted(configurationDirectory, temporaryName))
        {
            // Windows applies its length limits to the whole path rather than to one component, and
            // a file system with a longer component limit accepts both names, so neither can place
            // the failure between the reservation and the write.
            return;
        }

        var store = new BootstrapFileStore(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseProviderRegistry([]),
            Path.Combine(configurationDirectory, fileName));

        var failure = Assert.Throws<BootstrapException>(() => store.Create(CreateConfiguration()));

        // The reservation is the target path itself, so the declared release is observable only as
        // the absence of that file after a create that had already claimed it.
        Assert.Equal(BootstrapFileFailureKind.Unavailable, failure.FailureKind);
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.TryLoad());
        Assert.Empty(Directory.GetFileSystemEntries(configurationDirectory));
    }

    [Fact]
    public void A_denied_probe_is_never_reported_as_a_missing_target()
    {
        if (OperatingSystem.IsWindows())
        {
            // The Unix directory-permission construction below has no portable Windows equivalent
            // that is guaranteed to make File.Exists lie, so the case is not asserted there.
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, "denied");
        store.Create(CreateConfiguration());
        var configurationDirectory = Path.GetDirectoryName(store.FilePath)!;

        File.SetUnixFileMode(configurationDirectory, UnixFileMode.None);
        try
        {
            if (File.Exists(store.FilePath))
            {
                // A caller with enough privilege to bypass directory permissions (root) cannot
                // construct the denial this case is about.
                return;
            }

            // File.Exists is false only because the directory cannot be traversed. The target is
            // still there, so classifying this as TargetMissing would be a false statement.
            var replaceFailure = Assert.Throws<BootstrapException>(
                () => store.Replace(CreateConfiguration()));
            Assert.Equal(BootstrapFileFailureKind.Unavailable, replaceFailure.FailureKind);

            var loadFailure = Assert.Throws<BootstrapException>(() => store.Load());
            Assert.Equal(BootstrapFileFailureKind.Unavailable, loadFailure.FailureKind);
        }
        finally
        {
            File.SetUnixFileMode(
                configurationDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Manager_pre_cancellation_and_candidate_rejection_keep_their_existing_behavior()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, "guarded");
        store.Create(CreateConfiguration());
        var original = File.ReadAllBytes(store.FilePath);

        // A caller that cancels before the operation starts still gets a cancellation, never a
        // classified bootstrap failure.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var rejectingValidator = new RejectingValidator();
        var manager = CreateManager(store, rejectingValidator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.CreateAsync(CreateRequest(), cancelled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement"),
                cancelled.Token).AsTask());
        Assert.Equal(0, rejectingValidator.CallCount);

        // Candidate rejection is a management failure, not a file classification, and publishes
        // nothing.
        await Assert.ThrowsAsync<BootstrapManagementException>(() =>
            manager.UpdateAsync(
                new BootstrapUpdateRequest(replacementMasterKey: "replacement"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, rejectingValidator.CallCount);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void The_classification_value_carries_no_constructed_secret()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, "safe");
        store.Create(CreateConfiguration());

        var failure = Assert.Throws<BootstrapException>(() => store.Create(CreateConfiguration()));

        // Only the projected classification is asserted here; Message, FilePath and InnerException
        // stay diagnostic and are deliberately not given a new redaction guarantee.
        var projection = failure.FailureKind.ToString();
        Assert.DoesNotContain(ConnectionSecret, projection, StringComparison.Ordinal);
        Assert.DoesNotContain(MasterSecret, projection, StringComparison.Ordinal);
        Assert.DoesNotContain(store.FilePath, projection, StringComparison.Ordinal);
        Assert.Equal("TargetAlreadyExists", projection);
    }

    private sealed class RejectingValidator : IBootstrapCandidateValidator
    {
        public int CallCount { get; private set; }

        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(BootstrapValidationResult.Failure("candidate.rejected"));
        }
    }

    /// <summary>
    /// Reports whether the file system accepts a name, proving which of the two create steps can
    /// reach the disk at all.
    /// </summary>
    private static bool NameIsAccepted(string directoryPath, string fileName)
    {
        var path = Path.Combine(directoryPath, fileName);
        try
        {
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static BootstrapFileStore CreateStore(
        TemporaryDirectory directory,
        string caseName,
        ServiceId? serviceId = null)
    {
        var filePath = Path.Combine(directory.Path, caseName, "signacore.bootstrap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return new BootstrapFileStore(
            serviceId ?? ServiceId.Parse("signacore"),
            new BootstrapDatabaseProviderRegistry([]),
            filePath);
    }

    private static BootstrapConfigurationManager CreateManager(
        BootstrapFileStore store,
        IBootstrapCandidateValidator? validator = null) =>
        new(store, InstanceId.Parse("Node-A3"), validator ?? new AcceptingValidator());

    private static BootstrapCreateRequest CreateRequest() =>
        new(new BootstrapDatabaseConfiguration("PostgreSQL", "15", ConnectionSecret), MasterSecret);

    private static BootstrapConfiguration CreateConfiguration(
        ServiceId? serviceId = null,
        string connectionString = ConnectionSecret) =>
        new(
            serviceId ?? ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration("PostgreSQL", "15", connectionString),
            MasterSecret);

    private sealed class AcceptingValidator : IBootstrapCandidateValidator
    {
        public ValueTask<BootstrapValidationResult> ValidateAsync(
            BootstrapConfiguration candidate,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(BootstrapValidationResult.Success());
    }
}

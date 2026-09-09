using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// Reads and safely persists the instance-local bootstrap file for one service.
/// </summary>
public sealed class BootstrapFileStore
{
    private const int CurrentFormatVersion = 1;
    private const int BufferSize = 4096;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ServiceId serviceId;
    private readonly BootstrapDatabaseProviderRegistry providerRegistry;

    /// <summary>
    /// Initializes a store using the default path or an explicit path.
    /// </summary>
    /// <param name="serviceId">The expected service identifier.</param>
    /// <param name="providerRegistry">
    /// The provider registry that owns this host's provider registrations. Every value written to
    /// and read from the file is canonicalized through its
    /// <see cref="BootstrapDatabaseProviderRegistry.ProviderIdResolver"/>, so a registered alias
    /// never reaches disk or a capability lookup, and the store can only ever agree with the
    /// registry that dispatches the candidate validation.
    /// </param>
    /// <param name="filePath">An optional bootstrap file path.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="serviceId"/> or <paramref name="providerRegistry"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">The explicit path is empty.</exception>
    public BootstrapFileStore(
        ServiceId serviceId,
        BootstrapDatabaseProviderRegistry providerRegistry,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerRegistry);

        this.serviceId = serviceId;
        this.providerRegistry = providerRegistry;
        FilePath = ResolveFilePath(serviceId, filePath);
    }

    /// <summary>
    /// Initializes a store from a service identifier string.
    /// </summary>
    /// <param name="serviceId">The expected service identifier.</param>
    /// <param name="providerRegistry">The provider registry that owns this host's registrations.</param>
    /// <param name="filePath">An optional bootstrap file path.</param>
    public BootstrapFileStore(
        string serviceId,
        BootstrapDatabaseProviderRegistry providerRegistry,
        string? filePath = null)
        : this(ServiceId.Parse(serviceId), providerRegistry, filePath)
    {
    }

    /// <summary>
    /// Resolves the absolute bootstrap file path a store would use, without constructing one.
    /// </summary>
    /// <param name="serviceId">The expected service identifier.</param>
    /// <param name="filePath">An optional bootstrap file path.</param>
    /// <remarks>
    /// Hosts that must know the path before the provider registry snapshot exists use this instead
    /// of constructing a store early; the store itself is then built lazily with the final snapshot.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    /// <exception cref="ArgumentException">The explicit path is empty.</exception>
    public static string ResolveFilePath(ServiceId serviceId, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        var candidatePath = filePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "config",
            $"{serviceId.Value}.bootstrap.json");

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new ArgumentException("The bootstrap file path cannot be empty.", nameof(filePath));
        }

        return Path.GetFullPath(candidatePath);
    }

    /// <summary>
    /// Gets the service identifier expected in the bootstrap file.
    /// </summary>
    public ServiceId ServiceId => serviceId;

    /// <summary>
    /// Gets the provider registry this store shares its provider registrations with.
    /// </summary>
    public BootstrapDatabaseProviderRegistry ProviderRegistry => providerRegistry;

    /// <summary>
    /// Gets the provider-id resolver snapshot this store canonicalizes through.
    /// </summary>
    /// <remarks>
    /// This is the registry's own snapshot instance, never a separately constructed copy.
    /// </remarks>
    public DatabaseProviderIdResolver ProviderIdResolver => providerRegistry.ProviderIdResolver;

    /// <summary>
    /// Gets the absolute path of the bootstrap file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Loads the bootstrap file when it exists.
    /// </summary>
    /// <returns>The loaded configuration, or null when the file does not exist.</returns>
    /// <remarks>
    /// The target is opened read-only and shares read and delete access, so a reader this store
    /// holds does not by itself keep <see cref="Replace"/> from publishing over the target. The
    /// reader keeps observing the file it opened; a replacement becomes visible to the next open.
    /// Sharing delete permits another handle to request delete or rename; it grants this handle no
    /// write or delete access of its own, and it is not a guarantee about handles this store does
    /// not own.
    /// </remarks>
    /// <exception cref="BootstrapException">
    /// The file exists but is invalid or inaccessible. A damaged, mismatched, or unreadable file is
    /// <see cref="BootstrapFileFailureKind.Unavailable"/>; a missing file returns null instead.
    /// </exception>
    public BootstrapConfiguration? TryLoad()
    {
        try
        {
            using var stream = OpenTargetForRead();

            var document = JsonSerializer.Deserialize<BootstrapJsonDocument>(stream, ReadOptions);
            return ToConfiguration(document);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var bytePosition = exception.BytePositionInLine ?? 0;
            throw Failure(
                $"contains invalid JSON at line {line}, byte position {bytePosition}.");
        }
        catch (BootstrapException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure("cannot be read because access was denied.", exception);
        }
        catch (IOException exception)
        {
            throw Failure("could not be read because of an I/O error.", exception);
        }
    }

    /// <summary>
    /// Opens the bootstrap file for reading, using the single sharing mode every read of the target
    /// goes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryLoad"/> and therefore <see cref="Load"/> and the manager status read all open
    /// the target here, so there is one place where the target's read sharing is decided.
    /// </para>
    /// <para>
    /// The handle shares read and delete. Delete sharing is what an atomic replace of the target
    /// needs while this handle is open: on Windows, <c>ReplaceFileW</c> opens the replaced target
    /// with <c>DELETE</c> access, and a handle that does not share delete makes that open fail. The
    /// flag widens what other handles may ask for; it adds no write or delete access here.
    /// </para>
    /// </remarks>
    internal FileStream OpenTargetForRead() =>
        new(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);

    /// <summary>
    /// Loads the bootstrap file and fails when it does not exist.
    /// </summary>
    /// <returns>The loaded bootstrap configuration.</returns>
    /// <exception cref="BootstrapException">
    /// The file is missing, invalid, or inaccessible. An absence the store proved by opening the
    /// file is <see cref="BootstrapFileFailureKind.TargetMissing"/>; every other failure is
    /// <see cref="BootstrapFileFailureKind.Unavailable"/>.
    /// </exception>
    public BootstrapConfiguration Load() =>
        TryLoad() ?? throw Failure(
            "does not exist.",
            failureKind: BootstrapFileFailureKind.TargetMissing);

    /// <summary>
    /// Creates a new bootstrap file and never overwrites an existing file.
    /// </summary>
    /// <param name="configuration">The configuration to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="BootstrapException">
    /// The target exists or the file cannot be written. A target the operating system refused to
    /// publish over is <see cref="BootstrapFileFailureKind.TargetAlreadyExists"/>, including the
    /// target a concurrent creator won first; every other failure, including a link the file system
    /// refused, is <see cref="BootstrapFileFailureKind.Unavailable"/>.
    /// </exception>
    public void Create(BootstrapConfiguration configuration) =>
        Persist(configuration, replace: false);

    /// <summary>
    /// Atomically replaces an existing bootstrap file.
    /// </summary>
    /// <param name="configuration">The configuration to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="BootstrapException">
    /// The target does not exist or the file cannot be replaced. An absence the store proved by
    /// opening the file is <see cref="BootstrapFileFailureKind.TargetMissing"/>; every other failure,
    /// including a denied or failed probe, is <see cref="BootstrapFileFailureKind.Unavailable"/>.
    /// </exception>
    public void Replace(BootstrapConfiguration configuration) =>
        Persist(configuration, replace: true);

    private BootstrapConfiguration ToConfiguration(BootstrapJsonDocument? document)
    {
        if (document is null)
        {
            throw Failure("is empty or does not contain a JSON object.");
        }

        if (document.FormatVersion is not null && document.FormatVersion != CurrentFormatVersion)
        {
            throw Failure("uses an unsupported format version.");
        }

        ServiceId fileServiceId;
        if (document.ServiceId is null)
        {
            fileServiceId = serviceId;
        }
        else
        {
            if (!ServiceId.TryParse(document.ServiceId, out var parsedServiceId) || parsedServiceId is null)
            {
                throw Failure("contains an invalid ServiceId.");
            }

            fileServiceId = parsedServiceId;
        }

        if (fileServiceId != serviceId)
        {
            throw Failure("belongs to a different service.");
        }

        if (document.Database is null)
        {
            throw Failure("does not contain the required Database section.");
        }

        if (document.Database.Provider is null)
        {
            throw Failure("does not contain a database Provider.");
        }

        if (document.Database.ConnectionString is null ||
            string.IsNullOrWhiteSpace(document.Database.ConnectionString))
        {
            throw Failure("does not contain a database ConnectionString.");
        }

        if (document.MasterKey is null || string.IsNullOrWhiteSpace(document.MasterKey))
        {
            throw Failure("does not contain a MasterKey.");
        }

        // Reading canonicalizes in memory only. An existing file that still holds a registered
        // alias is never rewritten by a read; it becomes canonical on disk at the next Replace().
        if (!ProviderIdResolver.TryCanonicalize(document.Database.Provider, out var canonicalProvider))
        {
            throw Failure("contains an unsupported or invalid database configuration.");
        }

        BootstrapDatabaseConfiguration database;
        try
        {
            database = new BootstrapDatabaseConfiguration(
                canonicalProvider,
                document.Database.ServerVersion,
                document.Database.ConnectionString);
        }
        catch (ArgumentException)
        {
            throw Failure("contains an unsupported or invalid database configuration.");
        }

        try
        {
            return new BootstrapConfiguration(fileServiceId, database, document.MasterKey, FilePath);
        }
        catch (ArgumentException)
        {
            throw Failure("contains an invalid bootstrap configuration.");
        }
    }

    private void Persist(BootstrapConfiguration configuration, bool replace)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.ServiceId != serviceId)
        {
            throw Failure("cannot be written for a different service.");
        }

        var directoryPath = Path.GetDirectoryName(FilePath)!;
        string? temporaryPath = null;

        try
        {
            if (replace)
            {
                EnsureReplaceTargetExists();
            }
            else
            {
                EnsurePrivateDirectory(directoryPath);
            }

            temporaryPath = Path.Combine(
                directoryPath,
                $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

            WriteTemporaryFile(temporaryPath, configuration, ProviderIdResolver);

            if (replace)
            {
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);

                // The replace consumed the temporary name, so there is nothing left to remove.
                temporaryPath = null;
            }
            else
            {
                // The content is complete before the target exists at all. The temporary name stays
                // set so the finally block removes this call's second name for the published file.
                PublishNewFile(temporaryPath);
            }
        }
        catch (BootstrapException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure("could not be written because access was denied.", exception);
        }
        catch (IOException exception)
        {
            throw Failure("could not be written because of an I/O error.", exception);
        }
        finally
        {
            // The temporary file is only ever this call's own work, whether the operation failed
            // before it was published or succeeded and left it as a second name for the target. A
            // refused delete leaves that file beside the target and changes nothing else; the
            // target path itself is never removed here.
            if (temporaryPath is not null)
            {
                Discard(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Publishes the completed file at the create target, which must still be free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one operating system step both decides the single winner among concurrent creators and
    /// makes the file appear complete. It replaces a preceding existence check, which cannot do
    /// either: <see cref="File.Move(string, string)"/> without overwrite is a check followed by a
    /// rename on Unix, so two creators can both observe an absent target and both rename, and the
    /// second silently replaces the first - breaking the promise that <see cref="Create"/> never
    /// overwrites an existing file.
    /// </para>
    /// <para>
    /// Nothing is placed at the target before the content is complete, so a create that fails, or a
    /// process that dies at any point, leaves the target exactly as it found it. What can be left
    /// behind is the temporary file beside it, which no operation reads and a later create replaces
    /// with a fresh name.
    /// </para>
    /// <para>
    /// A refusal is classified only from the operating system's own answer. A target that is
    /// already taken is reported as such by the link itself, which is positive evidence rather than
    /// an inference from a separate observation; every other refusal, including a file system that
    /// does not support links at all, leaves the cause unestablished and is
    /// <see cref="BootstrapFileFailureKind.Unavailable"/>.
    /// </para>
    /// </remarks>
    private void PublishNewFile(string temporaryPath)
    {
        switch (HardLinkPublisher.TryPublish(temporaryPath, FilePath, out var errorCode))
        {
            case HardLinkResult.Published:
                return;

            case HardLinkResult.TargetAlreadyExists:
                throw Failure(
                    "already exists and cannot be overwritten by Create.",
                    failureKind: BootstrapFileFailureKind.TargetAlreadyExists);

            default:
                throw Failure(
                    $"could not be published because the file system refused to link it "
                    + $"(operating system error {errorCode}).");
        }
    }

    /// <summary>
    /// Removes a file this call owns, as a best effort.
    /// </summary>
    /// <remarks>
    /// A refused delete is swallowed so the operation reports its own classified failure instead of
    /// the cleanup's. No decision in the store depends on the delete having succeeded, so a refused
    /// release leaves the file in place rather than changing what the caller is told.
    /// </remarks>
    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Proves the replace target exists by opening it.
    /// </summary>
    /// <remarks>
    /// A negative <see cref="File.Exists(string)"/> result also means "the path could not be
    /// inspected", so it can never be the evidence for
    /// <see cref="BootstrapFileFailureKind.TargetMissing"/>. Opening the file separates the two: the
    /// operating system reporting the file or its directory as not found is proof of absence, while
    /// a denied or failed open leaves the cause unestablished and reaches the caller as
    /// <see cref="BootstrapFileFailureKind.Unavailable"/> through the surrounding handlers.
    /// </remarks>
    private void EnsureReplaceTargetExists()
    {
        try
        {
            using var probe = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.None);
        }
        catch (FileNotFoundException exception)
        {
            throw Failure(
                "cannot be replaced because it does not exist.",
                exception,
                BootstrapFileFailureKind.TargetMissing);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw Failure(
                "cannot be replaced because it does not exist.",
                exception,
                BootstrapFileFailureKind.TargetMissing);
        }
    }

    private static void EnsurePrivateDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directoryPath);
        }
        else
        {
            Directory.CreateDirectory(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void WriteTemporaryFile(
        string temporaryPath,
        BootstrapConfiguration configuration,
        DatabaseProviderIdResolver providerIdResolver)
    {
        var document = new BootstrapJsonDocument
        {
            FormatVersion = CurrentFormatVersion,
            ServiceId = configuration.ServiceId.Value,
            Database = new BootstrapJsonDatabase
            {
                // A registered alias is resolved to the declaring descriptor id before it reaches
                // disk; an unregistered but syntactically valid id is persisted as declared.
                Provider = providerIdResolver.Canonicalize(configuration.Database.Provider),
                ServerVersion = configuration.Database.ServerVersion,
                ConnectionString = configuration.Database.ConnectionString
            },
            MasterKey = configuration.MasterKey
        };

        using var stream = OpenNewPrivateFile(temporaryPath);

        JsonSerializer.Serialize(stream, document, WriteOptions);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Creates a file that must not already exist, owner-readable and owner-writable only.
    /// </summary>
    private static FileStream OpenNewPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan);
        }

        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
    }

    private BootstrapException Failure(
        string detail,
        Exception? innerException = null,
        BootstrapFileFailureKind failureKind = BootstrapFileFailureKind.Unavailable) =>
        new(FilePath, $"Bootstrap file '{FilePath}' {detail}", innerException, failureKind);
}

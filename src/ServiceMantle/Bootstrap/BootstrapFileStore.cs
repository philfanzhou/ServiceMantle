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
    /// <exception cref="BootstrapException">
    /// The file exists but is invalid or inaccessible. A damaged, mismatched, or unreadable file is
    /// <see cref="BootstrapFileFailureKind.Unavailable"/>; a missing file returns null instead.
    /// </exception>
    public BootstrapConfiguration? TryLoad()
    {
        try
        {
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);

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
    /// The target exists or the file cannot be written. An existing target the store observed is
    /// <see cref="BootstrapFileFailureKind.TargetAlreadyExists"/>; a write that lost the
    /// non-overwriting publish to a concurrent creator, or failed for any cause the store could not
    /// establish, is <see cref="BootstrapFileFailureKind.Unavailable"/>.
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
        var holdsReservation = false;

        try
        {
            if (replace)
            {
                EnsureReplaceTargetExists();
            }
            else
            {
                EnsurePrivateDirectory(directoryPath);
                ReserveNewFile();
                holdsReservation = true;
            }

            temporaryPath = Path.Combine(
                directoryPath,
                $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

            WriteTemporaryFile(temporaryPath, configuration, ProviderIdResolver);

            if (replace)
            {
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
            }
            else
            {
                // Overwriting is safe here and only here: the destination is the empty reservation
                // this call owns, and no other creator can hold it.
                File.Move(temporaryPath, FilePath, overwrite: true);
                holdsReservation = false;
            }

            temporaryPath = null;
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
            if (temporaryPath is not null)
            {
                Discard(temporaryPath);
            }

            // A reservation that never received its content is this call's own empty file, so
            // releasing it leaves no target behind for a create this call saw fail. A process that
            // never reaches here leaves the empty reservation on the target path.
            if (holdsReservation)
            {
                Discard(FilePath);
            }
        }
    }

    /// <summary>
    /// Claims the create target with the operating system's atomic exclusive create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This, not a preceding existence check, is what decides the single winner among concurrent
    /// creators. <see cref="File.Move(string, string)"/> without overwrite is a check followed by a
    /// rename on Unix, so two creators can both observe an absent target and both rename, and the
    /// second silently replaces the first - which would break the promise that
    /// <see cref="Create"/> never overwrites an existing file. An exclusive create is performed by
    /// the operating system in one step, so exactly one caller can hold the target.
    /// </para>
    /// <para>
    /// The reservation is empty and is renamed over by the completed file. A reader that observes
    /// the target during that window sees an incomplete file and gets
    /// <see cref="BootstrapFileFailureKind.Unavailable"/>, never a partially written configuration.
    /// </para>
    /// <para>
    /// The reservation belongs to the call that took it, and only that call releases it. A process
    /// aborted between the reservation and the rename leaves an empty file at the target path,
    /// which the store does not reclaim: a later <see cref="Create"/> reports
    /// <see cref="BootstrapFileFailureKind.TargetAlreadyExists"/> and a read reports
    /// <see cref="BootstrapFileFailureKind.Unavailable"/> until that file is removed.
    /// </para>
    /// </remarks>
    private void ReserveNewFile()
    {
        try
        {
            using var reservation = OpenNewPrivateFile(FilePath);
        }
        catch (IOException exception) when (File.Exists(FilePath))
        {
            // Positive evidence: the exclusive create failed and the target is demonstrably there.
            throw Failure(
                "already exists and cannot be overwritten by Create.",
                exception,
                BootstrapFileFailureKind.TargetAlreadyExists);
        }
    }

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

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

    /// <summary>
    /// Initializes a store using the default path or an explicit path.
    /// </summary>
    /// <param name="serviceId">The expected service identifier.</param>
    /// <param name="filePath">An optional bootstrap file path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    /// <exception cref="ArgumentException">The explicit path is empty.</exception>
    public BootstrapFileStore(ServiceId serviceId, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        this.serviceId = serviceId;
        var candidatePath = filePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "config",
            $"{serviceId.Value}.bootstrap.json");

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new ArgumentException("The bootstrap file path cannot be empty.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(candidatePath);
    }

    /// <summary>
    /// Initializes a store from a service identifier string.
    /// </summary>
    /// <param name="serviceId">The expected service identifier.</param>
    /// <param name="filePath">An optional bootstrap file path.</param>
    public BootstrapFileStore(string serviceId, string? filePath = null)
        : this(ServiceId.Parse(serviceId), filePath)
    {
    }

    /// <summary>
    /// Gets the service identifier expected in the bootstrap file.
    /// </summary>
    public ServiceId ServiceId => serviceId;

    /// <summary>
    /// Gets the absolute path of the bootstrap file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Loads the bootstrap file when it exists.
    /// </summary>
    /// <returns>The loaded configuration, or null when the file does not exist.</returns>
    /// <exception cref="BootstrapException">The file exists but is invalid or inaccessible.</exception>
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
    /// <exception cref="BootstrapException">The file is missing, invalid, or inaccessible.</exception>
    public BootstrapConfiguration Load() =>
        TryLoad() ?? throw Failure("does not exist.");

    /// <summary>
    /// Creates a new bootstrap file and never overwrites an existing file.
    /// </summary>
    /// <param name="configuration">The configuration to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="BootstrapException">The target exists or the file cannot be written.</exception>
    public void Create(BootstrapConfiguration configuration) =>
        Persist(configuration, replace: false);

    /// <summary>
    /// Atomically replaces an existing bootstrap file.
    /// </summary>
    /// <param name="configuration">The configuration to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="BootstrapException">The target does not exist or the file cannot be replaced.</exception>
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

        BootstrapDatabaseConfiguration database;
        try
        {
            database = new BootstrapDatabaseConfiguration(
                document.Database.Provider,
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
                if (!File.Exists(FilePath))
                {
                    throw Failure("cannot be replaced because it does not exist.");
                }
            }
            else
            {
                if (File.Exists(FilePath))
                {
                    throw Failure("already exists and cannot be overwritten by Create.");
                }

                EnsurePrivateDirectory(directoryPath);
            }

            temporaryPath = Path.Combine(
                directoryPath,
                $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

            WriteTemporaryFile(temporaryPath, configuration);

            if (replace)
            {
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
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
                try
                {
                    File.Delete(temporaryPath);
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

    private static void WriteTemporaryFile(string temporaryPath, BootstrapConfiguration configuration)
    {
        var document = new BootstrapJsonDocument
        {
            FormatVersion = CurrentFormatVersion,
            ServiceId = configuration.ServiceId.Value,
            Database = new BootstrapJsonDatabase
            {
                Provider = configuration.Database.Provider,
                ServerVersion = configuration.Database.ServerVersion,
                ConnectionString = configuration.Database.ConnectionString
            },
            MasterKey = configuration.MasterKey
        };

        using var stream = OpenTemporaryFile(temporaryPath);

        JsonSerializer.Serialize(stream, document, WriteOptions);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenTemporaryFile(string temporaryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan);
        }

        return new FileStream(
            temporaryPath,
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

    private BootstrapException Failure(string detail, Exception? innerException = null) =>
        new(FilePath, $"Bootstrap file '{FilePath}' {detail}", innerException);
}

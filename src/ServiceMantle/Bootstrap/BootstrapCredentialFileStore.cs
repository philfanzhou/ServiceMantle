using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// The instance-local file implementation of <see cref="IBootstrapCredentialStore"/>.
/// </summary>
/// <remarks>
/// The record holds only a format version, the versioned digest, and the issuance and expiry
/// timestamps. The plaintext credential never reaches disk. Provisioning uses an exclusive create so
/// that only one caller wins, and consumption claims the record with a cross-process atomic rename
/// and re-checks the claimed content before it succeeds. Every file call is synchronous: the caller's
/// token is observed at each boundary, but a synchronous file operation already in progress cannot
/// be interrupted, and no wall-clock bound is promised for it.
/// </remarks>
public sealed class BootstrapCredentialFileStore : IBootstrapCredentialStore
{
    /// <summary>The current credential record format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The largest accepted raw record size, in bytes.</summary>
    public const int MaximumFileByteCount = 4096;

    /// <summary>The largest accepted JSON nesting depth of a record.</summary>
    public const int MaximumJsonDepth = 4;

    private const string FormatVersionName = "formatVersion";
    private const string DigestName = "digest";
    private const string IssuedAtName = "issuedAtUtc";
    private const string ExpiresAtName = "expiresAtUtc";
    private const int BufferSize = 4096;

    /// <summary>
    /// How many times a refused read of a record is re-observed before it is reported as a failure.
    /// </summary>
    private const int MaximumReadObservations = 32;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
    };

    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a store using the default paths or explicit paths.</summary>
    /// <param name="serviceId">The service the credential belongs to.</param>
    /// <param name="filePath">An optional explicit credential record path.</param>
    /// <param name="bootstrapFilePath">
    /// An optional explicit Bootstrap file path. The store only tests whether that file exists; it
    /// never reads, returns, or modifies its connection string or MasterKey.
    /// </param>
    /// <param name="timeProvider">The clock used for issuance and expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    /// <exception cref="ArgumentException">An explicit path is empty.</exception>
    public BootstrapCredentialFileStore(
        ServiceId serviceId,
        string? filePath = null,
        string? bootstrapFilePath = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ServiceId = serviceId;
        FilePath = ResolveFilePath(serviceId, filePath);
        BootstrapFilePath = BootstrapFileStore.ResolveFilePath(serviceId, bootstrapFilePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the service the credential belongs to.</summary>
    public ServiceId ServiceId { get; }

    /// <summary>Gets the absolute path of the credential record.</summary>
    public string FilePath { get; }

    /// <summary>Gets the absolute Bootstrap file path this store checks for existence.</summary>
    public string BootstrapFilePath { get; }

    /// <summary>
    /// Resolves the absolute credential record path a store would use, without constructing one.
    /// </summary>
    /// <param name="serviceId">The service the credential belongs to.</param>
    /// <param name="filePath">An optional explicit path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    /// <exception cref="ArgumentException">The explicit path is empty.</exception>
    public static string ResolveFilePath(ServiceId serviceId, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        var candidatePath = filePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "config",
            $"{serviceId.Value}.bootstrap-credential.json");
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new ArgumentException(
                "The Bootstrap credential file path cannot be empty.",
                nameof(filePath));
        }

        return Path.GetFullPath(candidatePath);
    }

    /// <inheritdoc />
    public ValueTask<BootstrapCredentialProvisionResult> ProvisionAsync(
        BootstrapCredentialLifetime lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Provision(lifetime, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<BootstrapCredentialStatusResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadStatus(cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<BootstrapCredentialConsumptionResult> ConsumeAsync(
        string? candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Consume(candidate, cancellationToken));
    }

    private BootstrapCredentialProvisionResult Provision(
        BootstrapCredentialLifetime lifetime,
        CancellationToken cancellationToken)
    {
        bool bootstrapConfigured;
        try
        {
            bootstrapConfigured = File.Exists(BootstrapFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BootstrapCredentialProvisionResult.Rejected(
                WellKnownBootstrapCredentialErrorCodes.Unavailable);
        }

        if (bootstrapConfigured)
        {
            return BootstrapCredentialProvisionResult.Rejected(
                WellKnownBootstrapCredentialErrorCodes.BootstrapConfigured);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var credential = BootstrapCredential.Generate();
        var issuedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = issuedAtUtc + lifetime.Value;
        var payload = Serialize(BootstrapCredentialDigest.Compute(credential), issuedAtUtc, expiresAtUtc);

        try
        {
            EnsurePrivateDirectory(Path.GetDirectoryName(FilePath)!);
            // Exclusive creation is the concurrency decision: the operating system admits exactly
            // one writer, in this process or any other, and the loser never sees a plaintext.
            using var stream = OpenExclusiveCreate(FilePath);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (Exists(FilePath))
        {
            return BootstrapCredentialProvisionResult.Rejected(
                WellKnownBootstrapCredentialErrorCodes.AlreadyExists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BootstrapCredentialProvisionResult.Rejected(
                WellKnownBootstrapCredentialErrorCodes.Unavailable);
        }

        return BootstrapCredentialProvisionResult.Provisioned(credential, issuedAtUtc, expiresAtUtc);
    }

    private BootstrapCredentialStatusResult ReadStatus(CancellationToken cancellationToken)
    {
        var bootstrapConfigured = Exists(BootstrapFilePath);
        byte[]? content;
        try
        {
            content = TryReadRecord(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BootstrapCredentialStatusResult.Absent(
                BootstrapCredentialStatus.Unavailable,
                bootstrapConfigured);
        }

        if (content is null)
        {
            return BootstrapCredentialStatusResult.Absent(
                BootstrapCredentialStatus.NotProvisioned,
                bootstrapConfigured);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseRecord(content, out var record))
        {
            return BootstrapCredentialStatusResult.Absent(
                BootstrapCredentialStatus.Unavailable,
                bootstrapConfigured);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        return BootstrapCredentialStatusResult.Existing(
            nowUtc >= record!.ExpiresAtUtc
                ? BootstrapCredentialStatus.Expired
                : BootstrapCredentialStatus.Provisioned,
            record.IssuedAtUtc,
            record.ExpiresAtUtc,
            bootstrapConfigured);
    }

    private BootstrapCredentialConsumptionResult Consume(
        string? candidate,
        CancellationToken cancellationToken)
    {
        byte[]? content;
        try
        {
            content = TryReadRecord(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Unavailable);
        }

        if (content is null)
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Invalid);
        }

        if (!TryParseRecord(content, out var record))
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Unavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!BootstrapCredential.TryParse(candidate, out var parsed) || parsed is null)
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Invalid);
        }

        // The fixed-time comparison happens before anything is claimed, so an invalid candidate can
        // never consume a valid credential.
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (!record!.Digest.Matches(parsed) || nowUtc >= record.ExpiresAtUtc)
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Invalid);
        }

        var claimPath = Path.Combine(
            Path.GetDirectoryName(FilePath)!,
            $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.consumed");
        try
        {
            // The rename is the one-time boundary: at most one caller in any process moves the
            // record away, and every later caller finds nothing to claim.
            File.Move(FilePath, claimPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return Rejected(WellKnownBootstrapCredentialErrorCodes.Invalid);
        }
        catch (UnauthorizedAccessException)
        {
            // A refused rename only proves that this caller did not claim the record. Windows
            // reports a claim that is already in flight on the same record as an access denial
            // rather than as an absence, so the record is re-observed instead of guessed at: one
            // that is gone was claimed by somebody else, which is the caller's ordinary invalid
            // result, while one that is still there leaves the denial unexplained and stays a
            // storage failure.
            return Rejected(WasClaimedElsewhere(FilePath)
                ? WellKnownBootstrapCredentialErrorCodes.Invalid
                : WellKnownBootstrapCredentialErrorCodes.Unavailable);
        }

        try
        {
            byte[]? claimed;
            try
            {
                claimed = TryReadRecord(claimPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Rejected(WellKnownBootstrapCredentialErrorCodes.Unavailable);
            }

            if (claimed is null || !TryParseRecord(claimed, out var claimedRecord))
            {
                return Rejected(WellKnownBootstrapCredentialErrorCodes.Unavailable);
            }

            // A record replaced between the read and the claim is not the credential the caller
            // presented, so the claim fails closed and the moved record stays consumed.
            return claimedRecord!.Digest.Matches(parsed) && nowUtc < claimedRecord.ExpiresAtUtc
                ? BootstrapCredentialConsumptionResult.Consumed()
                : Rejected(WellKnownBootstrapCredentialErrorCodes.Invalid);
        }
        finally
        {
            TryDelete(claimPath);
        }
    }

    private static BootstrapCredentialConsumptionResult Rejected(string errorCode) =>
        BootstrapCredentialConsumptionResult.Rejected(errorCode);

    /// <summary>
    /// Re-observes the record through the same read boundary after a refused claim, and reports
    /// whether it is gone.
    /// </summary>
    /// <remarks>
    /// Only a proven absence answers true. A record that is still readable, and a re-observation
    /// that is itself refused, both leave the refusal unexplained, so the caller keeps the storage
    /// failure rather than being told its candidate was merely invalid.
    /// </remarks>
    private static bool WasClaimedElsewhere(string path)
    {
        try
        {
            return TryReadRecord(path) is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Serialize(
        BootstrapCredentialDigest digest,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber(FormatVersionName, FormatVersion);
        writer.WriteString(DigestName, digest.Value);
        writer.WriteString(IssuedAtName, ToWireValue(issuedAtUtc));
        writer.WriteString(ExpiresAtName, ToWireValue(expiresAtUtc));
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string ToWireValue(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Opens a credential record for reading, using the one sharing mode every read of a record
    /// goes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The initial read, the status read, and the re-check of the claimed record all open here, so
    /// there is a single place where a record's read sharing is decided.
    /// </para>
    /// <para>
    /// The handle is read-only and shares read and delete. Delete sharing is what the one-time
    /// claim needs: the claim renames the record, which on Windows requires <c>DELETE</c> access on
    /// it, and two handles are compatible only when each one's share mode covers what the other was
    /// granted. Without it a reader and a concurrent claim refuse each other, and a refused read
    /// turns a merely invalid candidate into <c>bootstrap_credential.unavailable</c>. The flag
    /// widens what other handles may request; it grants this handle no write or delete access, and
    /// it relaxes no file permission.
    /// </para>
    /// </remarks>
    internal static FileStream OpenRecordForRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);

    /// <summary>
    /// Reads the raw record, or returns null when it does not exist. An oversized file is read only
    /// far enough to prove that it is oversized.
    /// </summary>
    /// <remarks>
    /// A refused open is re-observed a bounded number of times before it is reported. The claim is
    /// a rename, and on Windows the rename holds the record for an instant without sharing read, so
    /// an open beside one in flight is refused however this reader shares. That refusal is not
    /// evidence of a storage failure: the claim either completes, and the next open reports the
    /// record as absent - which is the true answer, and the caller's ordinary invalid result - or it
    /// does not, and the refusal survives the re-observations and is reported unchanged. Nothing
    /// here reclassifies a corrupt, oversized, or genuinely inaccessible record.
    /// </remarks>
    private static byte[]? TryReadRecord(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = OpenRecordForRead(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception exception) when (
                attempt < MaximumReadObservations &&
                exception is IOException or UnauthorizedAccessException)
            {
                // One millisecond per observation, so the whole bound stays a few tens of
                // milliseconds and no wall-clock guarantee is created by it.
                Thread.Sleep(1);
                continue;
            }

            using (stream)
            {
                var buffer = new byte[MaximumFileByteCount + 1];
                var read = 0;
                while (read < buffer.Length)
                {
                    var current = stream.Read(buffer, read, buffer.Length - read);
                    if (current == 0)
                    {
                        break;
                    }

                    read += current;
                }

                return buffer[..read];
            }
        }
    }

    private static bool TryParseRecord(byte[] content, out StoredCredentialRecord? record)
    {
        record = null;
        if (content.Length is 0 or > MaximumFileByteCount)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, DocumentOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            int? formatVersion = null;
            string? digestValue = null;
            string? issuedAtValue = null;
            string? expiresAtValue = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // A duplicate member is rejected instead of resolved by position, so a record can
                // never be read differently by two readers.
                if (!seen.Add(property.Name))
                {
                    return false;
                }

                switch (property.Name)
                {
                    case FormatVersionName:
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var version))
                        {
                            return false;
                        }

                        formatVersion = version;
                        break;
                    case DigestName:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        digestValue = property.Value.GetString();
                        break;
                    case IssuedAtName:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        issuedAtValue = property.Value.GetString();
                        break;
                    case ExpiresAtName:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        expiresAtValue = property.Value.GetString();
                        break;
                    default:
                        return false;
                }
            }

            if (formatVersion != FormatVersion ||
                !BootstrapCredentialDigest.TryParse(digestValue, out var digest) ||
                digest is null ||
                !TryParseTimestamp(issuedAtValue, out var issuedAtUtc) ||
                !TryParseTimestamp(expiresAtValue, out var expiresAtUtc) ||
                expiresAtUtc <= issuedAtUtc)
            {
                return false;
            }

            record = new StoredCredentialRecord(digest, issuedAtUtc, expiresAtUtc);
            return true;
        }
    }

    private static bool TryParseTimestamp(string? value, out DateTime timestamp)
    {
        timestamp = default;
        if (value is null ||
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The record is already outside the credential path, so it can no longer be consumed.
        }
        catch (UnauthorizedAccessException)
        {
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

    private static FileStream OpenExclusiveCreate(string path)
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
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
    }

    private sealed record StoredCredentialRecord(
        BootstrapCredentialDigest Digest,
        DateTime IssuedAtUtc,
        DateTime ExpiresAtUtc);
}

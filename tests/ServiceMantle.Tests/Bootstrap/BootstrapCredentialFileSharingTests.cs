using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the sharing mode a credential record is read with, the one-time claim that has to be
/// able to rename that record while such a read handle is open, and the claim re-check that holds
/// the claimed record without sharing delete.
/// </summary>
/// <remarks>
/// Windows decides handle compatibility from both sides: a new open is admitted only when every
/// existing handle's share mode covers what it asks for, and when its own share mode covers what
/// those handles were already granted. The claim renames the record, which needs <c>DELETE</c> on
/// it, so a read that does not share delete and a concurrent claim refuse each other. Unix renames
/// never consult open handles, so a Unix run cannot show the difference and is a plain regression
/// only.
/// </remarks>
public sealed class BootstrapCredentialFileSharingTests
{
    /// <summary>ERROR_SHARING_VIOLATION.</summary>
    private const int WindowsSharingViolation = 32;

    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsSynchronize = 0x00100000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsAttributeNormal = 0x00000080;

    /// <summary>FileRenameInfo, the SetFileInformationByHandle class that renames by handle.</summary>
    private const uint WindowsFileRenameInfo = 3;

    private static readonly ServiceId Service = ServiceId.Parse("credential-worker");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_claim_succeeds_while_the_store_holds_its_own_read_handle()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();

        // The production read open point, opened before the consumption starts and closed after it
        // returns. The overlap is the arrangement of the test, not a race it hopes to hit.
        using var reader = BootstrapCredentialFileStore.OpenRecordForRead(store.FilePath);

        var result = await Create(directory).ConsumeAsync(candidate, Token);

        Assert.True(result.IsConsumed);
        Assert.False(File.Exists(store.FilePath));

        // The holder still observes the record it opened, in full.
        using var document = JsonDocument.Parse(ReadFromStart(reader));
        Assert.Equal(
            BootstrapCredentialFileStore.FormatVersion,
            document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            BootstrapCredentialDigest.Compute(provisioned.Credential!).Value,
            document.RootElement.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task A_claim_is_refused_while_a_read_handle_without_delete_sharing_is_open_on_windows()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);
        var candidate = provisioned.Credential!.Reveal();

        // The same arrangement as the test above with the production sharing flags reverted, so the
        // pair is a before/after contrast rather than a single passing assertion.
        using var legacyReader = OpenRecordWithoutDeleteSharing(store.FilePath);

        var result = await Create(directory).ConsumeAsync(candidate, Token);

        if (OperatingSystem.IsWindows())
        {
            // The rename cannot take DELETE on a record held without delete sharing, and the store
            // classifies a refused claim as invalid. The record is left where it was.
            Assert.False(result.IsConsumed);
            Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, result.ErrorCode);
            Assert.True(File.Exists(store.FilePath));
            Assert.DoesNotContain(candidate, result.ToString(), StringComparison.Ordinal);
            return;
        }

        // A Unix rename does not consult open handles, so this half is a read and claim regression
        // only. Passing here is not evidence about the Windows sharing rules; only the Windows job
        // produces that.
        Assert.True(result.IsConsumed);
    }

    /// <summary>
    /// Names the read stage the reported Windows failure came from: <c>Consume</c> reads the record
    /// before it validates the candidate, so a refused read turned a merely wrong candidate into
    /// <c>unavailable</c> instead of <c>invalid</c>.
    /// </summary>
    [Fact]
    public async Task A_read_beside_a_claim_shaped_handle_is_refused_only_without_delete_sharing()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        var provisioned = await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        if (!OperatingSystem.IsWindows())
        {
            // There is no Unix equivalent of a handle that reserves delete access, so there is
            // nothing here a Unix run could confirm or refute.
            Assert.True(provisioned.IsProvisioned);
            return;
        }

        // A handle granted exactly the access the claim's rename takes on the record.
        using var claimShaped = OpenWithClaimAccess(store.FilePath);

        var refused = Assert.Throws<IOException>(() => OpenRecordWithoutDeleteSharing(store.FilePath));
        Assert.Equal(WindowsSharingViolation, refused.HResult & 0xFFFF);

        using (var reader = BootstrapCredentialFileStore.OpenRecordForRead(store.FilePath))
        {
            using var document = JsonDocument.Parse(ReadFromStart(reader));
            Assert.Equal(
                BootstrapCredentialFileStore.FormatVersion,
                document.RootElement.GetProperty("formatVersion").GetInt32());
        }

        var wrongCandidate = await store.ConsumeAsync(BootstrapCredential.Generate().Reveal(), Token);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, wrongCandidate.ErrorCode);

        // The status read goes through the same open point and reports the record, not a failure.
        var status = await store.GetStatusAsync(Token);
        Assert.Equal(BootstrapCredentialStatus.Provisioned, status.Status);
    }

    /// <summary>
    /// Names the stage, the system error, and the before/after contrast of the one interleaving
    /// that could turn a single claim into two consumptions on Windows: a stale handle granted
    /// <c>DELETE</c> before the winner's rename lands, renaming the claimed record on through
    /// itself.
    /// </summary>
    [Fact]
    public async Task A_stale_delete_handle_cannot_steal_the_claimed_record_into_a_second_consumption()
    {
        using var claimDirectory = TemporaryDirectory.Create();
        var claimStore = Create(claimDirectory);
        var claimProvisioned = await claimStore.ProvisionAsync(
            BootstrapCredentialLifetime.Default,
            Token);
        var claimCandidate = claimProvisioned.Credential!.Reveal();

        if (!OperatingSystem.IsWindows())
        {
            // There is no Unix equivalent of a handle that reserves delete access, so there is
            // nothing here a Unix run could confirm or refute.
            return;
        }

        // The pre-fix mechanism, deterministically: this handle mimics the losing claim's source
        // handle, which shares delete - that is what lets the winner's rename land beside it.
        using (var stale = OpenClaimSourceHandle(claimStore.FilePath))
        {
            var firstClaim = Path.Combine(claimDirectory.Path, ".credential.json.first.consumed");
            File.Move(claimStore.FilePath, firstClaim);

            // The production read of the claimed record is admitted beside the stale handle, so
            // the pre-fix re-check read the record and reported a consumption.
            using (var reader = BootstrapCredentialFileStore.OpenRecordForRead(firstClaim))
            using (var document = JsonDocument.Parse(ReadFromStart(reader)))
            {
                Assert.Equal(
                    BootstrapCredentialDigest.Compute(claimProvisioned.Credential!).Value,
                    document.RootElement.GetProperty("digest").GetString());
            }

            // The steal: the same file object renamed on through the stale handle, after the
            // pre-fix re-check had passed. Without the claim guard this is the second consumption.
            var stolenPath = Path.Combine(claimDirectory.Path, ".credential.json.stolen");
            RenameThroughHandle(stale, stolenPath);
            Assert.False(File.Exists(firstClaim));
            Assert.True(File.Exists(stolenPath));
        }

        // The fixed store under the same arrangement: the claim still lands, but the claim guard
        // is refused for as long as the stale handle holds the claimed record, so the caller is
        // not told the credential was consumed.
        using var guardDirectory = TemporaryDirectory.Create();
        var guardStore = Create(guardDirectory);
        var guardProvisioned = await guardStore.ProvisionAsync(
            BootstrapCredentialLifetime.Default,
            Token);
        var guardCandidate = guardProvisioned.Credential!.Reveal();

        using var guardStale = OpenClaimSourceHandle(guardStore.FilePath);
        var refused = await Create(guardDirectory).ConsumeAsync(guardCandidate, Token);

        Assert.False(refused.IsConsumed);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, refused.ErrorCode);
        Assert.DoesNotContain(guardCandidate, refused.ToString(), StringComparison.Ordinal);

        // The claim landed - the record is gone from the credential path - and the claim guard's
        // failure path left the claimed record in place instead of deleting it.
        Assert.False(File.Exists(guardStore.FilePath));
        var claimed = Assert.Single(Directory.GetFiles(guardDirectory.Path, "*.consumed"));

        // The guard open itself is the refused stage, with the sharing violation as its error.
        var guardRefusal = Assert.Throws<IOException>(
            () => BootstrapCredentialFileStore.OpenClaimGuard(claimed));
        Assert.Equal(WindowsSharingViolation, guardRefusal.HResult & 0xFFFF);

        // The steal stays reachable through the stale handle, which is exactly why the guard has
        // to refuse: the fix does not stop the steal, it stops the store from calling a stolen
        // record its own consumption. The stolen record dies outside the credential path.
        var stolenFromGuard = Path.Combine(guardDirectory.Path, ".credential.json.stolen");
        RenameThroughHandle(guardStale, stolenFromGuard);
        Assert.False(File.Exists(claimed));
        Assert.True(File.Exists(stolenFromGuard));

        // Nobody else can consume the credential through the record path afterwards, and the
        // ordinary loser answer is invalid, not another storage failure.
        var later = await Create(guardDirectory).ConsumeAsync(guardCandidate, Token);
        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Invalid, later.ErrorCode);
    }

    [Fact]
    public async Task A_read_failure_that_is_not_a_sharing_conflict_stays_unavailable()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        // A directory at the record path cannot be opened as a file on any platform, so the store
        // reports a storage failure rather than downgrading it to invalid.
        Directory.CreateDirectory(store.FilePath);

        var consumed = await store.ConsumeAsync(BootstrapCredential.Generate().Reveal(), Token);
        var status = await store.GetStatusAsync(Token);

        Assert.Equal(WellKnownBootstrapCredentialErrorCodes.Unavailable, consumed.ErrorCode);
        Assert.Equal(BootstrapCredentialStatus.Unavailable, status.Status);
    }

    [Fact]
    public async Task The_record_read_handle_grants_read_access_only()
    {
        using var directory = TemporaryDirectory.Create();
        var store = Create(directory);
        await store.ProvisionAsync(BootstrapCredentialLifetime.Default, Token);

        using var reader = BootstrapCredentialFileStore.OpenRecordForRead(store.FilePath);

        Assert.True(reader.CanRead);
        Assert.False(reader.CanWrite);

        if (!OperatingSystem.IsWindows())
        {
            // Sharing delete widens what other handles may ask for; it changes no file permission.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.FilePath));
        }
    }

    /// <summary>
    /// Opens the record the way the store did before the sharing modes were aligned.
    /// </summary>
    private static FileStream OpenRecordWithoutDeleteSharing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

    /// <summary>
    /// Opens the record with the access a rename takes on it, so a read can be tested against a
    /// real pending claim instead of a timing window.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenWithClaimAccess(string path)
    {
        var handle = CreateFile(
            path,
            WindowsDelete | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"The claim-shaped handle could not be opened (operating system error "
                + $"{Marshal.GetLastWin32Error()}).");
        }

        return handle;
    }

    /// <summary>
    /// Opens the record the way a claim's own rename does: holding <c>DELETE</c> while sharing
    /// read, write, and delete, so another claim's rename can land beside it and the record stays
    /// movable through this handle alone.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenClaimSourceHandle(string path)
    {
        var handle = CreateFile(
            path,
            WindowsDelete | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"The claim source handle could not be opened (operating system error "
                + $"{Marshal.GetLastWin32Error()}).");
        }

        return handle;
    }

    /// <summary>
    /// Renames the file an open handle belongs to, without opening any path, which is how a stale
    /// handle moves a record no name can reach anymore.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RenameThroughHandle(SafeFileHandle handle, string newPath)
    {
        // FILE_RENAME_INFO on x64: a 4-byte ReplaceIfExists of zero, 4 bytes of alignment padding,
        // an 8-byte null RootDirectory, a 4-byte name length in characters, then the wide name.
        var info = new byte[20 + checked(newPath.Length * 2)];
        System.Text.Encoding.Unicode.GetBytes(newPath).CopyTo(info, 20);
        BitConverter.GetBytes(newPath.Length).CopyTo(info, 16);

        if (!SetFileInformationByHandle(handle, WindowsFileRenameInfo, info, (uint)info.Length))
        {
            throw new IOException(
                $"Renaming through the handle failed (operating system error "
                + $"{Marshal.GetLastWin32Error()}).");
        }
    }

    private static string ReadFromStart(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static BootstrapCredentialFileStore Create(TemporaryDirectory directory) => new(
        Service,
        Path.Combine(directory.Path, "credential.json"),
        Path.Combine(directory.Path, "bootstrap.json"));

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle,
        uint fileInformationClass,
        byte[] fileInformation,
        uint length);
}

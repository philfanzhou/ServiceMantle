using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the sharing mode a credential record is read with, and the one-time claim that has to be
/// able to rename that record while such a read handle is open.
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
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsAttributeNormal = 0x00000080;

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

    [Fact]
    public async Task Sixteen_contenders_produce_exactly_one_success_in_every_round()
    {
        const int Rounds = 20;
        const int Contenders = 16;

        for (var round = 0; round < Rounds; round++)
        {
            using var directory = TemporaryDirectory.Create();
            var provisioned = await Create(directory).ProvisionAsync(
                BootstrapCredentialLifetime.Default,
                Token);
            var candidate = provisioned.Credential!.Reveal();
            var candidates = Enumerable.Range(0, Contenders)
                .Select(index => index % 2 == 0 ? candidate : BootstrapCredential.Generate().Reveal())
                .ToArray();
            using var start = new Barrier(candidates.Length);

            var results = await Task.WhenAll(candidates.Select(value => Task.Run(async () =>
            {
                var store = Create(directory);
                start.SignalAndWait(Token);
                return await store.ConsumeAsync(value, Token);
            }, Token)));

            Assert.Single(results, result => result.IsConsumed);
            Assert.All(
                results.Where(result => !result.IsConsumed),
                result => Assert.Equal(
                    WellKnownBootstrapCredentialErrorCodes.Invalid,
                    result.ErrorCode));
        }
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
}

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Reads the raw login body into memory before the consumer adapter runs, so the admitted envelope
/// is decided by the bytes that actually arrived rather than by a declared length or a host feature
/// the server may not provide.
/// </summary>
/// <remarks>
/// <para>
/// At most one byte past the limit is ever read: that byte proves the request is over the envelope
/// and nothing beyond it is buffered. The buffer grows from a small rented block and is never sized
/// from <c>Content-Length</c>, and no part of the body reaches a temporary file.
/// </para>
/// <para>
/// While the adapter runs, <see cref="HttpRequest.Body"/> and an
/// <see cref="IRequestBodyPipeFeature"/> reader obtained inside that call both read the admitted
/// copy. The original stream and feature are put back afterwards; this type completes only the
/// reader it created itself and never disposes or completes the host's own body or reader, and it
/// does not promise to restore the original stream's position.
/// </para>
/// </remarks>
internal sealed class ServiceMantleManagementSessionBodyAdmission : IDisposable
{
    /// <summary>The most bytes this helper ever reads: one past the admitted envelope.</summary>
    internal const int ProbeLength =
        (int)ServiceMantleManagementSessionMapping.MaximumLoginBodyLength + 1;

    private const int InitialCapacity = 8 * 1024;

    private readonly byte[]? rented;
    private readonly int length;
    private HttpContext? installed;
    private Stream? originalBody;
    private IRequestBodyPipeFeature? originalPipe;
    private AdmittedBodyPipeFeature? admittedPipe;
    private MemoryStream? admittedBody;
    private bool disposed;

    private ServiceMantleManagementSessionBodyAdmission(
        ServiceMantleManagementSessionBodyStatus status,
        byte[]? rented = null,
        int length = 0)
    {
        Status = status;
        this.rented = rented;
        this.length = length;
    }

    /// <summary>The three ways the raw body admission can finish.</summary>
    internal enum ServiceMantleManagementSessionBodyStatus
    {
        /// <summary>The whole body was read and fits the envelope.</summary>
        Admitted,

        /// <summary>More bytes arrived than the envelope admits, or the host rejected the body.</summary>
        Rejected,

        /// <summary>The body could not be read: an I/O failure, or the login budget was spent.</summary>
        Unavailable
    }

    internal ServiceMantleManagementSessionBodyStatus Status { get; }

    /// <summary>The admitted body length in bytes; zero unless the body was admitted.</summary>
    internal int Length => length;

    /// <summary>
    /// Reads the request body up to <see cref="ProbeLength"/> bytes under the login budget.
    /// </summary>
    /// <remarks>
    /// A body the host itself refused with <c>413</c> is the same rejection as a locally counted
    /// overrun. Every other failure, including the spent budget and an internal cancellation, is the
    /// unavailable outcome; the caller's own cancellation is decided by the handler, which holds the
    /// request token.
    /// </remarks>
    internal static async ValueTask<ServiceMantleManagementSessionBodyAdmission> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        var count = 0;
        try
        {
            while (count < ProbeLength)
            {
                if (count == buffer.Length)
                {
                    buffer = Grow(buffer, count);
                }

                var read = await request.Body
                    .ReadAsync(
                        buffer.AsMemory(count, Math.Min(buffer.Length, ProbeLength) - count),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    // End of the body inside the envelope: this is the complete request body, and
                    // a short read followed by more data cannot be mistaken for it.
                    return new ServiceMantleManagementSessionBodyAdmission(
                        ServiceMantleManagementSessionBodyStatus.Admitted,
                        buffer,
                        count);
                }

                count += read;
            }

            // The extra byte arrived, so more than the envelope was sent whatever follows it.
            return Release(buffer, ServiceMantleManagementSessionBodyStatus.Rejected);
        }
        catch (BadHttpRequestException rejected)
            when (rejected.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The host counted the same overrun first. It is the request that is too large, not the
            // service that is unavailable.
            return Release(buffer, ServiceMantleManagementSessionBodyStatus.Rejected);
        }
        catch
        {
            // An I/O failure, a spent budget, and an unrelated internal cancellation are one safe
            // outcome, and no host or transport detail is projected.
            return Release(buffer, ServiceMantleManagementSessionBodyStatus.Unavailable);
        }
    }

    /// <summary>
    /// Points the request at the admitted copy for the duration of the adapter call, remembering
    /// the stream and the pipe feature it replaced.
    /// </summary>
    internal void Install(HttpContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Status != ServiceMantleManagementSessionBodyStatus.Admitted || installed is not null)
        {
            return;
        }

        admittedBody = new MemoryStream(rented!, 0, length, writable: false, publiclyVisible: false);
        originalBody = context.Request.Body;
        originalPipe = context.Features.Get<IRequestBodyPipeFeature>();
        admittedPipe = new AdmittedBodyPipeFeature(admittedBody);
        context.Request.Body = admittedBody;

        // Kestrel answers the request's pipe reader from its own feature, so replacing the stream
        // alone would leave a reader obtained inside the adapter on the original body.
        context.Features.Set<IRequestBodyPipeFeature>(admittedPipe);
        installed = context;
    }

    /// <summary>
    /// Puts the original body stream and pipe feature back and releases what this helper created.
    /// The host's own stream and reader are left open and uncompleted.
    /// </summary>
    internal void Restore()
    {
        if (installed is not { } context)
        {
            return;
        }

        installed = null;
        try
        {
            context.Request.Body = originalBody!;
        }
        catch
        {
            // A request that refuses the restore is beyond this helper's reach; the admitted copy
            // is still released below so no login keeps a buffer alive.
        }

        try
        {
            // Setting a null feature removes it again, so a host that provided none keeps none.
            context.Features.Set(originalPipe);
        }
        catch
        {
            // Same as the stream above: the release below still runs.
        }

        originalBody = null;
        originalPipe = null;
        try
        {
            admittedPipe?.Complete();
        }
        catch
        {
            // Completing this helper's own reader is best effort; it owns nothing the host reads.
        }

        admittedPipe = null;
        admittedBody?.Dispose();
        admittedBody = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Restore();
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static byte[] Grow(byte[] current, int count)
    {
        var grown = ArrayPool<byte>.Shared.Rent(Math.Min(current.Length * 2, ProbeLength));
        current.AsSpan(0, count).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(current, clearArray: true);
        return grown;
    }

    private static ServiceMantleManagementSessionBodyAdmission Release(
        byte[] buffer,
        ServiceMantleManagementSessionBodyStatus status)
    {
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        return new ServiceMantleManagementSessionBodyAdmission(status);
    }

    /// <summary>
    /// Answers the request's pipe reader from the admitted copy. The reader is created on first use
    /// and is completed only by <see cref="Restore"/>, so an adapter that never asks for one pays
    /// for nothing.
    /// </summary>
    private sealed class AdmittedBodyPipeFeature(Stream body) : IRequestBodyPipeFeature
    {
        private PipeReader? reader;

        public PipeReader Reader =>
            reader ??= PipeReader.Create(body, new StreamPipeReaderOptions(leaveOpen: true));

        internal void Complete() => reader?.Complete();
    }
}

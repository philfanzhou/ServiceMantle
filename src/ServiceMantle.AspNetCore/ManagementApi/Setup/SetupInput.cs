using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.Installation;
using ServiceMantle.Logging;

namespace ServiceMantle.AspNetCore.ManagementApi.Setup;

/// <summary>
/// The consumer-defined installation input of one Setup completion request.
/// </summary>
/// <remarks>
/// <para>
/// The instance wraps exactly the <c>input</c> object of a structurally admitted
/// <c>{"code":...,"input":{...}}</c> completion body. ServiceMantle parses its shape but never
/// interprets, logs, echoes, or stores its content: the value is sensitive by definition, so the
/// type is marked <see cref="ISensitiveLogValue"/> and every string projection is fixed.
/// </para>
/// <para>
/// The endpoint owns the lifetime. The input stays valid only while the executor runs; once the
/// executor returned or threw, the endpoint disposes the underlying document and returns the zeroed
/// rented buffer to the pool, and any later access to <see cref="RootElement"/> throws
/// <see cref="ObjectDisposedException"/>. The type has no public constructor and no public
/// <c>Dispose</c>, so a consumer cannot extend the lifetime past the executor boundary.
/// </para>
/// </remarks>
[DebuggerDisplay("SetupInput(********)")]
public sealed class SetupInput : ISensitiveLogValue
{
    private readonly ArrayPool<byte> pool;
    private byte[]? buffer;
    private JsonDocument? document;

    internal SetupInput(ArrayPool<byte> pool, byte[] buffer, JsonDocument document)
    {
        this.pool = pool;
        this.buffer = buffer;
        this.document = document;
    }

    /// <summary>
    /// Gets the JSON object the request supplied as <c>input</c>, valid only while the executor runs.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The executor already returned or threw, so the input was released.
    /// </exception>
    public JsonElement RootElement =>
        (document ?? throw new ObjectDisposedException(nameof(SetupInput)))
            .RootElement
            .GetProperty(SetupRequestParser.InputPropertyName);

    internal void Release()
    {
        document?.Dispose();
        document = null;
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null)
        {
            // The buffer held the raw request bytes; it is zeroed before it returns and the pool
            // clear is kept as a second fence.
            Array.Clear(rented);
            pool.Return(rented, clearArray: true);
        }
    }

    /// <summary>Returns the fixed mask; the input content never reaches a string projection.</summary>
    public override string ToString() => "SetupInput(********)";
}

/// <summary>
/// Executes one Setup completion with consumer-defined installation input inside a consumer-owned
/// transaction and returns only after it has committed.
/// </summary>
/// <param name="httpContext">The current management HTTP request.</param>
/// <param name="setupCode">The strictly parsed candidate Setup Code. It is never logged or echoed.</param>
/// <param name="input">
/// The strictly shape-checked <c>input</c> object of the request. It is valid only for the duration
/// of this call and must not be retained afterwards.
/// </param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The closed completion result, produced only after the transaction settled.</returns>
/// <remarks>
/// <para>
/// The delegate keeps every obligation of <see cref="SetupExecutor"/>: a fresh asynchronous scope
/// with a clean <c>DbContext</c>, its own transaction, then the read-only
/// <c>IServiceSetupCodeStore.ValidateAsync</c>, <c>ServiceSetupOrchestrator</c>,
/// <c>StageConsumeAsync</c>, exactly one <c>SaveChangesAsync</c>, and a commit that must complete
/// before <see cref="SetupCompletionStatus.Committed"/> is returned.
/// </para>
/// <para>
/// One obligation is specific to input: the code is validated before the input's semantic content
/// may influence the answer. Only after the read-only <c>ValidateAsync</c> passed may a semantic
/// rejection of the input return <see cref="SetupCompletionStatus.ValidationFailed"/>; an invalid
/// code must return <see cref="SetupCompletionStatus.CredentialInvalid"/> so a caller without the
/// code cannot probe the input validation rules through the 400/401 split. Sensitive input values
/// may only be handed to the orchestrator's staging scope; they must never reach a log, an audit
/// description, or a response. The executor must not retain the <paramref name="input"/> or any
/// <see cref="JsonElement"/> derived from it after this delegate returns or throws.
/// </para>
/// </remarks>
public delegate ValueTask<SetupCompletionResult> SetupInputExecutor(
    HttpContext httpContext,
    SetupCode setupCode,
    SetupInput input,
    CancellationToken cancellationToken);

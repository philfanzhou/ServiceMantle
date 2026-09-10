using System.Text;

namespace ServiceMantle.ReleaseTool;

/// <summary>
/// Wraps a writer so that a known secret cannot reach it, whatever produced the text.
/// </summary>
/// <remarks>
/// <para>
/// Publishing is the one pipeline stage that holds a credential, and the text it prints is assembled
/// from feed diagnostics and exception messages this tool does not fully author. Redacting at the
/// single point where text leaves the process covers all of them at once, rather than relying on
/// every writing path to remember what it is holding.
/// </para>
/// <para>
/// The guarantee is bounded to one call: the character, string, or buffer handed to a single write
/// is scanned before it reaches the wrapped writer. This type never buffers, so a credential split
/// across two separate writes is not recognised. Every write path in this tool passes a whole
/// diagnostic in one call, which is what makes the bound sufficient here.
/// </para>
/// </remarks>
internal sealed class RedactingTextWriter(TextWriter inner, string? secret) : TextWriter
{
    private readonly string? secret = string.IsNullOrEmpty(secret) ? null : secret;

    public override Encoding Encoding => inner.Encoding;

    internal const string Replacement = "***";

    public override void Write(char value) => inner.Write(Redact(value.ToString()));

    public override void Write(string? value) => inner.Write(Redact(value));

    // The base class turns every remaining overload - char arrays, spans, StringBuilder chunks, and
    // their WriteLine forms - into one of these two, so overriding them keeps the buffer-shaped
    // paths from bypassing the redactor a character at a time.
    public override void Write(char[] buffer, int index, int count) =>
        inner.Write(Redact(new string(buffer, index, count)));

    public override void Write(ReadOnlySpan<char> buffer) => inner.Write(Redact(new string(buffer)));

    public override void WriteLine(string? value) => inner.WriteLine(Redact(value));

    public override void Flush() => inner.Flush();

    internal string? Redact(string? value) =>
        secret is null || value is null
            ? value
            : value.Replace(secret, Replacement, StringComparison.Ordinal);

    protected override void Dispose(bool disposing)
    {
        // The wrapped writer is Console.Out or a caller-owned buffer; this wrapper never owns it.
        base.Dispose(disposing);
    }
}

using System.Text;

namespace ServiceMantle.ReleaseTool;

/// <summary>
/// Wraps a writer so that a known secret cannot reach it, whatever produced the text.
/// </summary>
/// <remarks>
/// Publishing is the one pipeline stage that holds a credential, and the text it prints is assembled
/// from feed diagnostics and exception messages this tool does not fully author. Redacting at the
/// single point where text leaves the process is the only placement that covers all of them,
/// including a path added later that never considered the credential.
/// </remarks>
internal sealed class RedactingTextWriter(TextWriter inner, string? secret) : TextWriter
{
    private readonly string? secret = string.IsNullOrEmpty(secret) ? null : secret;

    public override Encoding Encoding => inner.Encoding;

    internal const string Replacement = "***";

    public override void Write(char value) => inner.Write(Redact(value.ToString()));

    public override void Write(string? value) => inner.Write(Redact(value));

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

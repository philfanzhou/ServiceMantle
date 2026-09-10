using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class RedactingTextWriterTests
{
    private const string Secret = "oy2-publish-credential-value";

    [Fact]
    public void Every_write_path_removes_the_credential()
    {
        var buffer = new StringWriter();
        var writer = new RedactingTextWriter(buffer, Secret);

        writer.Write($"key={Secret}");
        writer.WriteLine($" and again {Secret}");
        writer.Write('x');

        var written = buffer.ToString();
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
        Assert.Contains(RedactingTextWriter.Replacement, written, StringComparison.Ordinal);
        Assert.Contains("key=", written, StringComparison.Ordinal);
    }

    [Fact]
    public void No_credential_leaves_the_text_unchanged()
    {
        var buffer = new StringWriter();
        var writer = new RedactingTextWriter(buffer, secret: null);

        writer.WriteLine("published: ServiceMantle 0.1.0");

        Assert.Contains("published: ServiceMantle 0.1.0", buffer.ToString(), StringComparison.Ordinal);
    }
}

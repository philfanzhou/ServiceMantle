using System.Text;
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
    public void A_buffer_shaped_write_is_redacted_like_a_string_one()
    {
        // Left to the base class these all decompose into single characters, and a single character
        // can never match a credential, so the redaction would silently do nothing on these paths.
        var buffer = new StringWriter();
        var writer = new RedactingTextWriter(buffer, Secret);

        writer.Write($"array={Secret}".ToCharArray());
        writer.Write($"segment={Secret}".ToCharArray(), 0, $"segment={Secret}".Length);
        writer.Write($"span={Secret}".AsSpan());
        writer.WriteLine($"line={Secret}".ToCharArray());
        writer.WriteLine($"lineSpan={Secret}".AsSpan());
        writer.Write(new StringBuilder($"builder={Secret}"));
        writer.Write((object)$"boxed={Secret}");

        var written = buffer.ToString();
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
        foreach (var prefix in new[] { "array=", "segment=", "span=", "line=", "lineSpan=", "builder=", "boxed=" })
        {
            Assert.Contains($"{prefix}{RedactingTextWriter.Replacement}", written, StringComparison.Ordinal);
        }
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

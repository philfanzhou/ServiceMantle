namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// The one console seam the sample's Setup Code delivery writes through: the plaintext reaches
/// standard output (or the writer a test injects), never an <c>ILogger</c>.
/// </summary>
/// <remarks>
/// <para>
/// The default wraps <see cref="Console.Out"/> and <see cref="Console.Error"/>. A deployment that
/// wants the one-time code somewhere else replaces this singleton before the host starts; the
/// issuer and the rotation command resolve whatever is registered.
/// </para>
/// <para>
/// This seam exists because the Setup Code plaintext must not be formatted, enriched, or retained
/// by any logging pipeline the host composes. Whatever text a caller hands to
/// <see cref="Out"/>/<see cref="Error"/> is written verbatim and nothing is echoed back through
/// any other channel.
/// </para>
/// </remarks>
public sealed class ReferenceSetupCodeOutput
{
    /// <summary>Creates the seam over the two writers it owns nothing of.</summary>
    /// <param name="out">Where the one-time code banner is written.</param>
    /// <param name="error">Where fixed operator hints about the code are written.</param>
    public ReferenceSetupCodeOutput(TextWriter @out, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(@out);
        ArgumentNullException.ThrowIfNull(error);
        Out = @out;
        Error = error;
    }

    /// <summary>Gets the writer the code banner goes to.</summary>
    public TextWriter Out { get; }

    /// <summary>Gets the writer fixed failure hints go to.</summary>
    public TextWriter Error { get; }
}

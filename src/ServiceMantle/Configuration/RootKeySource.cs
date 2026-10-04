namespace ServiceMantle.Configuration;

/// <summary>Lazily resolves a captured root key, falling back to an explicit private local file.</summary>
/// <remarks>A non-whitespace injected value is returned unchanged without inspecting the file path.
/// File resolution uses <see cref="RootKeyFileSource"/> on every call, without caching its key or failures.
/// The caller owns the injected value's suitability, trusted directory and Windows ACLs.
/// Rotation, external replacement and in-memory string erasure are not guaranteed. Never log the returned secret.</remarks>
public sealed class RootKeySource
{
    private readonly string? injectedValue;
    private readonly string? filePath;
    private RootKeyFileSource? fileSource;

    /// <summary>Captures both inputs without filesystem I/O or path validation.</summary>
    /// <param name="injectedValue">The captured root key, used unchanged when non-whitespace.</param>
    /// <param name="filePath">The explicit file path used only when the injected value is absent.</param>
    public RootKeySource(string? injectedValue, string? filePath)
    {
        this.injectedValue = injectedValue;
        this.filePath = filePath;
    }

    /// <summary>Returns the captured injected value or resolves the shared file winner.</summary>
    /// <exception cref="InvalidOperationException">The fallback file cannot safely be resolved.
    /// Diagnostics contain no path or secret.</exception>
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(injectedValue)) return injectedValue;

        var source = Volatile.Read(ref fileSource);
        if (source is null)
        {
            var candidate = new RootKeyFileSource(filePath!);
            source = Interlocked.CompareExchange(ref fileSource, candidate, null) ?? candidate;
        }

        return source.Resolve();
    }

    /// <summary>Returns fixed metadata without either captured input or the resolved key.</summary>
    public override string ToString() => "RootKeySource(Lazy=True)";
}

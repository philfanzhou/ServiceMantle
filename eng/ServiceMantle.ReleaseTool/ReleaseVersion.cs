using NuGet.Versioning;

namespace ServiceMantle.ReleaseTool;

/// <summary>
/// The one place a release version is decided. The tag is the only input a maintainer controls, and
/// every version that leaves here has to survive NuGet's own normalization unchanged: a value NuGet
/// would rewrite (1.0, 1.0.0.0, 01.0.0) would be published under a different string than the tag,
/// which is a discrepancy nobody would notice until a consumer pinned the wrong one.
/// </summary>
internal static class ReleaseVersion
{
    internal const string TagPrefix = "v";

    /// <summary>Validates one release version and returns it unchanged.</summary>
    internal static string Require(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ReleaseToolException($"The {description} is missing.");
        }

        if (!NuGetVersion.TryParse(value, out var parsed))
        {
            throw new ReleaseToolException(
                $"The {description} '{value}' is not a version NuGet accepts.");
        }

        // Build metadata never reaches the NuGet feed: two versions differing only after '+' collide
        // as the same package, so accepting it would let one tag silently overwrite another's slot.
        if (parsed.HasMetadata)
        {
            throw new ReleaseToolException(
                $"The {description} '{value}' carries build metadata, which release does not support.");
        }

        if (!string.Equals(parsed.ToNormalizedString(), value, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(
                $"The {description} '{value}' is not in NuGet's normalized form " +
                $"('{parsed.ToNormalizedString()}').");
        }

        return value;
    }

    /// <summary>Decides the version a push of <paramref name="refName"/> should produce.</summary>
    /// <param name="refName">The pushed ref name, for example <c>v0.1.0-rc.1</c> or <c>main</c>.</param>
    /// <param name="tagged">Whether the ref is a tag.</param>
    /// <param name="untaggedVersion">The version to use when the ref is not a tag.</param>
    internal static ResolvedVersion Resolve(string refName, bool tagged, string untaggedVersion)
    {
        if (!tagged)
        {
            return new ResolvedVersion(Require(untaggedVersion, "untagged version"), Publish: false);
        }

        if (string.IsNullOrWhiteSpace(refName) ||
            !refName.StartsWith(TagPrefix, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(
                $"A release tag must start with '{TagPrefix}', for example v1.2.3 or v1.2.3-rc.1.");
        }

        return new ResolvedVersion(
            Require(refName[TagPrefix.Length..], "release tag version"),
            Publish: true);
    }
}

internal sealed record ResolvedVersion(string Number, bool Publish);

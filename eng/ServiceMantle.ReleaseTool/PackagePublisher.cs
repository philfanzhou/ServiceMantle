using System.IO.Compression;
using System.Xml.Linq;

namespace ServiceMantle.ReleaseTool;

internal enum PublishOutcome
{
    Published,
    AlreadyPresent,
    Failed,
}

internal sealed record PublishResult(string Id, string Version, PublishOutcome Outcome, string? Reason);

internal enum FeedLookup
{
    Missing,
    Found,
    Unauthorized,
    Failed,
}

internal sealed record FeedResponse(FeedLookup Lookup, byte[]? Content, string? Diagnostic)
{
    internal static FeedResponse Missing { get; } = new(FeedLookup.Missing, null, null);
}

internal enum PushStatus
{
    Succeeded,
    AlreadyPresent,
    Unauthorized,
    Failed,
}

internal sealed record PushResponse(PushStatus Status, string? Diagnostic)
{
    internal static PushResponse Succeeded { get; } = new(PushStatus.Succeeded, null);
}

/// <summary>Reads one already-published package back from the release feed.</summary>
internal interface IPackageFeed
{
    Task<FeedResponse> TryGetPackageAsync(string id, string version, CancellationToken cancellationToken);
}

/// <summary>Pushes one package or symbol artifact to the release feed.</summary>
internal interface IPackagePusher
{
    Task<PushResponse> PushAsync(string filePath, bool symbols, CancellationToken cancellationToken);
}

/// <summary>
/// Pushes the registered package set to a NuGet feed, one package at a time.
/// </summary>
/// <remarks>
/// <para>
/// A multi-package push is not a transaction. Any interruption leaves the feed holding some of the
/// set, so the design goal is not atomicity - it is that a rerun of the same version is safe and
/// finishes the job. That turns on one question per package: is the version already there, and is it
/// <em>ours</em>? A published package carries the repository commit it was built from, so comparing
/// that against the commit being published separates "this rerun already did this one" from
/// "someone else owns this version", and only the first is allowed to be skipped.
/// </para>
/// <para>
/// Every package is attempted even after one fails, so a single run reports the complete state of
/// the feed rather than stopping at the first problem and leaving the rest unknown.
/// </para>
/// </remarks>
internal static class PackagePublisher
{
    internal static async Task<IReadOnlyList<PublishResult>> PublishAsync(
        string root,
        PackageRegistry registry,
        string version,
        string commit,
        string input,
        IPackageFeed feed,
        IPackagePusher pusher,
        bool dryRun,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ReleaseVersion.Require(version, "release version");

        // Local artifacts are proven complete and correctly stamped before a single byte is pushed:
        // a partial or mislabelled set must fail while nothing is public yet.
        var inputPath = Program.ResolvePath(root, input, "package input");
        var missing = registry.Packages
            .SelectMany(package => new[] { "nupkg", "snupkg" }
                .Where(extension => !File.Exists(Path.Combine(inputPath, $"{package.Id}.{version}.{extension}")))
                .Select(extension => $"{package.Id} {version}: missing .{extension} artifact"))
            .ToArray();
        if (missing.Length > 0)
        {
            foreach (var diagnostic in missing)
            {
                output.WriteLine(diagnostic);
            }

            throw new ReleaseToolException("The registered package set is incomplete; no packages were pushed.");
        }

        ArtifactVerifier.Verify(root, registry, version, commit, input);

        var results = new List<PublishResult>(registry.Packages.Count);
        foreach (var package in registry.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await PublishPackageAsync(
                package,
                inputPath,
                version,
                commit,
                feed,
                pusher,
                dryRun,
                output,
                cancellationToken));
        }

        Report(results, dryRun, output);
        var failed = results.Count(result => result.Outcome == PublishOutcome.Failed);
        if (failed > 0)
        {
            throw new ReleaseToolException(
                $"{failed} of {results.Count} registered packages could not be published.");
        }

        return results;
    }

    private static async Task<PublishResult> PublishPackageAsync(
        RegisteredPackage package,
        string inputPath,
        string version,
        string commit,
        IPackageFeed feed,
        IPackagePusher pusher,
        bool dryRun,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var existing = await feed.TryGetPackageAsync(package.Id, version, cancellationToken);
        var alreadyPresent = existing.Lookup == FeedLookup.Found;
        if (existing.Lookup != FeedLookup.Missing)
        {
            var failure = ValidateExisting(package, version, commit, existing);
            if (failure is not null)
            {
                return failure;
            }
        }

        if (dryRun)
        {
            var outcome = alreadyPresent ? PublishOutcome.AlreadyPresent : PublishOutcome.Published;
            return new PublishResult(package.Id, version, outcome, null);
        }

        // An existing nupkg does not prove the symbols were uploaded. Always attempt the symbol
        // artifact after validating the package's origin, including on a partial-release rerun.
        foreach (var symbols in new[] { false, true })
        {
            if (!symbols && alreadyPresent)
            {
                continue;
            }

            var extension = symbols ? "snupkg" : "nupkg";
            var path = Path.Combine(inputPath, $"{package.Id}.{version}.{extension}");
            var response = await pusher.PushAsync(path, symbols, cancellationToken);
            var artifact = symbols ? "symbol package" : "package";
            switch (response.Status)
            {
                case PushStatus.Unauthorized:
                    return Failed(package, version, $"the feed rejected the credential for the {artifact}", response.Diagnostic);
                case PushStatus.Failed:
                    return Failed(package, version, $"the {artifact} push failed", response.Diagnostic);
                case PushStatus.AlreadyPresent:
                    // The read endpoint may lag behind the push endpoint. A conflict alone never
                    // proves ownership. If read-back cannot establish it, fail and allow a rerun.
                    var confirmed = await feed.TryGetPackageAsync(package.Id, version, cancellationToken);
                    var failure = ValidateExisting(package, version, commit, confirmed);
                    if (failure is not null)
                    {
                        return failure;
                    }

                    if (!symbols)
                    {
                        alreadyPresent = true;
                    }

                    break;
            }
        }

        var label = alreadyPresent ? "already present" : "published";
        output.WriteLine($"  {label}: {package.Id} {version}");
        return new PublishResult(
            package.Id, version,
            alreadyPresent ? PublishOutcome.AlreadyPresent : PublishOutcome.Published, null);
    }

    private static PublishResult? ValidateExisting(
        RegisteredPackage package,
        string version,
        string commit,
        FeedResponse existing)
    {
        switch (existing.Lookup)
        {
            case FeedLookup.Unauthorized:
                return Failed(package, version, "the feed rejected the credential", existing.Diagnostic);
            case FeedLookup.Failed:
                return Failed(package, version, "the feed could not be read", existing.Diagnostic);
            case FeedLookup.Missing:
                return Failed(package, version, "the conflicting package is not yet readable; retry later", null);
        }

        var origin = existing.Content is null ? null : ReadOrigin(existing.Content);
        if (origin is null || string.IsNullOrWhiteSpace(origin.Commit))
        {
            return Failed(package, version,
                "the already-published package does not declare unambiguous identity and repository commit metadata", null);
        }

        if (!string.Equals(origin.Id, package.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(origin.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(package, version, "the already-published package has a different ID or version", null);
        }

        return string.Equals(origin.Commit, commit, StringComparison.Ordinal)
            ? null
            : Failed(package, version,
                $"the already-published version was built from commit {origin.Commit}, not {commit}", null);
    }

    private static PublishResult Failed(
        RegisteredPackage package,
        string version,
        string reason,
        string? diagnostic) =>
        new(
            package.Id,
            version,
            PublishOutcome.Failed,
            diagnostic is { Length: > 0 } ? $"{reason} ({diagnostic})" : reason);

    /// <summary>Reads the repository commit a published package was built from.</summary>
    private static PackageOrigin? ReadOrigin(byte[] package)
    {
        try
        {
            using var stream = new MemoryStream(package, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var nuspec = archive.Entries.SingleOrDefault(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspec is null)
            {
                return null;
            }

            using var nuspecStream = nuspec.Open();
            var document = XDocument.Load(nuspecStream);
            var repository = document.Descendants().SingleOrDefault(element =>
                element.Name.LocalName == "repository");
            var id = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "id")?.Value;
            var version = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
            return new PackageOrigin(id, version, (string?)repository?.Attribute("commit"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record PackageOrigin(string? Id, string? Version, string? Commit);

    private static void Report(IReadOnlyList<PublishResult> results, bool dryRun, TextWriter output)
    {
        var mode = dryRun ? " (dry run)" : string.Empty;
        output.WriteLine($"Release publish summary{mode}:");
        foreach (var (outcome, label) in new[]
                 {
                     (PublishOutcome.Published, dryRun ? "would publish" : "published"),
                     (PublishOutcome.AlreadyPresent, "already present"),
                     (PublishOutcome.Failed, "failed"),
                 })
        {
            var matching = results.Where(result => result.Outcome == outcome).ToArray();
            output.WriteLine($"  {label}: {matching.Length}");
            foreach (var result in matching)
            {
                var reason = result.Reason is null ? string.Empty : $" - {result.Reason}";
                output.WriteLine($"    {result.Id} {result.Version}{reason}");
            }
        }
    }
}

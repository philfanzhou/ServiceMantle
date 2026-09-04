using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Sdk;

namespace ServiceMantle.Tests;

public sealed partial class SignaCoreLegacyMigrationManifestTests
{
    private static readonly string[] RequiredSubsystems =
    [
        "Audit",
        "Bootstrap",
        "Configuration",
        "Consul",
        "Health",
        "Migration",
        "Session",
        "Setup",
    ];

    private static readonly string[] RequiredScenarioCoverage =
    [
        "concurrency",
        "failure",
        "new-install",
        "restart",
        "security",
        "upgrade",
    ];

    private const string SnapshotBoundaryId = "product-schema-migrations";

    private const int EntitiesPerSnapshot = 18;

    private static readonly string[] RequiredSnapshotEntitySets =
    [
        "postgresql-model-snapshot",
        "sqlite-model-snapshot",
    ];

    private static readonly string[] BoundaryCoverageModes =
    [
        "path-and-symbol",
        "path-only",
    ];

    private static readonly string[] RequiredPreservedBoundaries =
    [
        "account-credential-domain",
        "admin-console-product-ux",
        "application-gateway-domain",
        "callback-domain",
        "ldap-domain",
        "login-history-domain",
        "oauth-jwt-token-domain",
        "product-schema-migrations",
        "signing-key-jwks-domain",
        "sms-otp-domain",
        "wechat-domain",
    ];

    [Fact]
    public void ManifestPinsAnAuditableSignaCoreBaselineAndExecutableCommands()
    {
        var manifest = LoadManifest();

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("https://github.com/philfanzhou/SignaCore", manifest.Source.Repository);
        Assert.Matches(CommitPattern(), manifest.Source.Commit);
        Assert.StartsWith(
            "https://github.com/philfanzhou/SignaCore/actions/runs/",
            manifest.Source.VerifiedCiRun,
            StringComparison.Ordinal);
        Assert.Equal("success", manifest.Source.VerifiedCiConclusion);
        Assert.Equal(["frontend", "integration", "unit"], manifest.Commands.Keys.Order());
        Assert.All(manifest.Commands.Values, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command));
            Assert.True(
                command.StartsWith("dotnet test --project ", StringComparison.Ordinal) ||
                command.StartsWith("npm --prefix ", StringComparison.Ordinal));
        });

        var referencedCommands = manifest.Candidates
            .SelectMany(candidate => candidate.Evidence)
            .Select(evidence => evidence.Command)
            .Concat(manifest.PreservedBoundaries
                .SelectMany(boundary => boundary.Tests)
                .Select(test => test.Command))
            .Distinct(StringComparer.Ordinal)
            .Order();
        Assert.Equal(manifest.Commands.Keys.Order(), referencedCommands);
    }

    [Fact]
    public void EveryDeletionCandidateHasEvidenceOneReplacementAndFailClosedGapRules()
    {
        var manifest = LoadManifest();
        Assert.Equal(
            RequiredSubsystems,
            manifest.Candidates.Select(candidate => candidate.Subsystem).Distinct().Order());
        Assert.Equal(
            manifest.Candidates.Count,
            manifest.Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var candidate in manifest.Candidates)
        {
            Assert.Matches(IdentifierPattern(), candidate.Id);
            Assert.Contains(candidate.Subsystem, RequiredSubsystems);
            Assert.True(candidate.LegacyPaths.Count + candidate.LegacySymbols.Count > 0);
            Assert.All(candidate.LegacyPaths.Concat(candidate.CallSites), AssertRepositoryRelativePath);
            Assert.All(candidate.LegacySymbols, symbol => Assert.Matches(LegacySymbolPattern(), symbol));
            Assert.NotEmpty(candidate.Evidence);
            Assert.True(
                IssueReferencePattern().IsMatch(candidate.Replacement.Reference) ||
                DottedIdentifierPattern().IsMatch(candidate.Replacement.Reference));
            Assert.Contains(candidate.Replacement.State, new[] { "implemented", "planned" });
            if (candidate.Replacement.State == "implemented")
            {
                AssertImplementedReplacementExists(candidate.Replacement.Reference);
            }
            Assert.NotEmpty(candidate.Prerequisites);
            Assert.All(candidate.Prerequisites, prerequisite =>
                Assert.Matches(IssueReferencePattern(), prerequisite));
            Assert.Contains(candidate.Disposition, new[] { "blocked", "ready" });
            Assert.All(candidate.CoverageGaps, gap => Assert.False(string.IsNullOrWhiteSpace(gap)));

            foreach (var evidence in candidate.Evidence)
            {
                Assert.True(manifest.Commands.ContainsKey(evidence.Command));
                Assert.Matches(TestNamePattern(), evidence.Test);
                Assert.NotEmpty(evidence.Scenarios);
                Assert.All(evidence.Scenarios, scenario =>
                    Assert.Matches(IdentifierPattern(), scenario));
            }

            if (candidate.Replacement.State == "planned" || candidate.CoverageGaps.Count > 0)
            {
                Assert.Equal("blocked", candidate.Disposition);
            }

            if (candidate.Disposition == "ready")
            {
                Assert.Equal("implemented", candidate.Replacement.State);
                Assert.Empty(candidate.CoverageGaps);
            }
        }

        var actualScenarios = manifest.Candidates
            .SelectMany(candidate => candidate.Evidence)
            .SelectMany(evidence => evidence.Scenarios)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(RequiredScenarioCoverage, scenario => Assert.Contains(scenario, actualScenarios));
    }

    [Fact]
    public void BatchMatrixIsCompleteSequentialAndBackedByCreatedSubIssues()
    {
        var manifest = LoadManifest();
        var batches = manifest.Batches.OrderBy(batch => batch.Order).ToArray();

        Assert.Equal(RequiredSubsystems.Length, batches.Length);
        Assert.Equal(Enumerable.Range(1, batches.Length), batches.Select(batch => batch.Order));
        Assert.Equal(
            RequiredSubsystems,
            batches.Select(batch => batch.Subsystem).Order());
        Assert.Equal(
            batches.Length,
            batches.Select(batch => batch.TrackingIssue).Distinct().Count());
        Assert.All(batches, batch =>
        {
            Assert.True(batch.TrackingIssue > 0);
            Assert.False(string.IsNullOrWhiteSpace(batch.ProposedTitle));
            Assert.NotEmpty(batch.CandidateIds);
            Assert.NotEmpty(batch.Prerequisites);
            Assert.All(batch.Prerequisites, prerequisite =>
                Assert.Matches(IssueReferencePattern(), prerequisite));
        });

        var batchedCandidateIds = batches.SelectMany(batch => batch.CandidateIds).ToArray();
        Assert.Equal(
            manifest.Candidates.Select(candidate => candidate.Id).Order(),
            batchedCandidateIds.Order());
        Assert.Equal(batchedCandidateIds.Length, batchedCandidateIds.Distinct().Count());

        foreach (var candidate in manifest.Candidates)
        {
            var batch = Assert.Single(batches, batch => batch.Id == candidate.Batch);
            Assert.Equal(candidate.Subsystem, batch.Subsystem);
            Assert.Contains(candidate.Id, batch.CandidateIds);
            Assert.All(candidate.Prerequisites, prerequisite =>
                Assert.Contains(prerequisite, batch.Prerequisites));
            Assert.DoesNotContain($"#{batch.TrackingIssue}", candidate.Prerequisites);
        }

        for (var index = 0; index < batches.Length; index++)
        {
            if (index > 0)
            {
                Assert.Contains($"#{batches[index - 1].TrackingIssue}", batches[index].Prerequisites);
            }

            var currentAndLaterTrackingIssues = batches[index..]
                .Select(batch => $"#{batch.TrackingIssue}")
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(
                batches[index].Prerequisites,
                currentAndLaterTrackingIssues.Contains);
        }
    }

    [Fact]
    public void PostDeletionAcceptanceCannotBecomeADeletionPrerequisite()
    {
        var manifest = LoadManifest();
        var acceptance = Assert.Single(manifest.PostDeletionAcceptance);

        Assert.Equal("#107", acceptance.Issue);
        Assert.Equal("#106", acceptance.AfterWorkstream);
        Assert.NotEmpty(acceptance.Scenarios);
        Assert.Contains("bootstrap-app-provisioning", acceptance.Scenarios);
        Assert.Matches(IssueReferencePattern(), acceptance.Issue);
        Assert.Matches(IssueReferencePattern(), acceptance.AfterWorkstream);

        foreach (var downstreamIssue in new[] { acceptance.Issue, acceptance.AfterWorkstream })
        {
            Assert.DoesNotContain(
                downstreamIssue,
                manifest.Candidates.SelectMany(candidate => candidate.Prerequisites));
            Assert.DoesNotContain(
                downstreamIssue,
                manifest.Batches.SelectMany(batch => batch.Prerequisites));
        }
    }

    [Fact]
    public void ProductBoundariesAreExplicitAndCannotOverlapWholeFileDeletionCandidates()
    {
        var manifest = LoadManifest();
        var boundaryIds = manifest.PreservedBoundaries
            .Select(boundary => boundary.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(RequiredPreservedBoundaries, id => Assert.Contains(id, boundaryIds));

        var preservedRoots = new List<string>();
        var preservedSymbolRoots = new List<string>();
        foreach (var boundary in manifest.PreservedBoundaries)
        {
            Assert.Matches(IdentifierPattern(), boundary.Id);
            Assert.NotEmpty(boundary.Paths);
            AssertBoundaryCoverage(boundary);
            Assert.NotEmpty(boundary.Tests);
            Assert.False(string.IsNullOrWhiteSpace(boundary.Rationale));
            Assert.All(boundary.PreservedSymbols, symbol =>
                Assert.Matches(DottedIdentifierPattern(), symbol));
            preservedSymbolRoots.AddRange(boundary.PreservedSymbols);
            Assert.All(boundary.Tests, test =>
            {
                Assert.True(manifest.Commands.ContainsKey(test.Command));
                Assert.Matches(TestNamePattern(), test.Test);
            });
            Assert.All(boundary.Paths, path =>
            {
                AssertRepositoryRelativePath(path);
                var preservedRoot = NormalizePathRoot(path);
                preservedRoots.Add(preservedRoot);
            });
        }

        foreach (var legacyPath in manifest.Candidates.SelectMany(candidate => candidate.LegacyPaths))
        {
            var legacyRoot = NormalizePathRoot(legacyPath);
            Assert.DoesNotContain(preservedRoots, preservedRoot =>
                RootsOverlap(legacyRoot, preservedRoot, '/'));
        }

        foreach (var legacySymbol in manifest.Candidates.SelectMany(candidate => candidate.LegacySymbols))
        {
            var legacySymbolRoot = legacySymbol.Split(": ", 2, StringSplitOptions.None)[0];
            Assert.DoesNotContain(preservedSymbolRoots, preservedSymbolRoot =>
                RootsOverlap(legacySymbolRoot, preservedSymbolRoot, '.'));
        }
    }

    [Fact]
    public void ManifestSchemaRejectsMissingOrMisspelledCoverageGaps()
    {
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "candidates": [{ "coverageGaps_TYPO": [] }] }
            """));
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "candidates": [{}] }
            """));
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "candidates": [{ "coverageGaps": [], "dispositionTypo": "ready" }] }
            """));
    }

    [Fact]
    public void KnownCoverageGapsBlockTheirDeletionBatches()
    {
        var manifest = LoadManifest();
        var consul = Assert.Single(
            manifest.Candidates,
            candidate => candidate.Id == "consul-registration-lifecycle");
        var adminSession = Assert.Single(
            manifest.Candidates,
            candidate => candidate.Id == "admin-cookie-session");
        var legacyUpgrade = Assert.Single(
            manifest.Candidates,
            candidate => candidate.Id == "legacy-configuration-upgrade");

        Assert.NotEmpty(consul.CoverageGaps);
        Assert.NotEmpty(adminSession.CoverageGaps);
        Assert.NotEmpty(legacyUpgrade.CoverageGaps);
        Assert.Equal("blocked", consul.Disposition);
        Assert.Equal("blocked", adminSession.Disposition);
        Assert.Equal("blocked", legacyUpgrade.Disposition);
    }

    [Fact]
    public void SnapshotEntitySetsPartitionEveryProviderSymbolExactlyOnce()
    {
        AssertSnapshotInventoryIsPartitioned(LoadManifest());
    }

    [Theory]
    [InlineData("missing-inventory-symbol")]
    [InlineData("duplicate-inventory-entry")]
    [InlineData("unassigned-symbol")]
    [InlineData("preserved-and-deleted")]
    [InlineData("divergent-snapshot-sets")]
    public void SnapshotPartitionGuardFailsOnEveryInventoryMutation(string mutation)
    {
        var manifest = LoadManifest();
        var boundary = SnapshotBoundary(manifest);

        switch (mutation)
        {
            case "missing-inventory-symbol":
                // A preserved symbol silently dropped leaves an inventory symbol unclaimed.
                boundary.PreservedSymbols.RemoveAt(0);
                break;
            case "duplicate-inventory-entry":
                manifest.SnapshotEntitySets[0].Entities[1] = manifest.SnapshotEntitySets[0].Entities[0];
                break;
            case "unassigned-symbol":
                // Renaming the same entity in both snapshots keeps the sets equal and the counts
                // right, but nothing in the manifest classifies the renamed symbol.
                foreach (var snapshot in manifest.SnapshotEntitySets)
                {
                    snapshot.Entities[0] = "GhostEntity";
                }

                break;
            case "preserved-and-deleted":
                boundary.PreservedSymbols.Add(DeletionSnapshotSymbols(manifest).First());
                break;
            case "divergent-snapshot-sets":
                manifest.SnapshotEntitySets[1].Entities[0] = "GhostEntity";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        Assert.ThrowsAny<XunitException>(() => AssertSnapshotInventoryIsPartitioned(manifest));
    }

    [Theory]
    [InlineData("duplicate-snapshot-path")]
    [InlineData("duplicate-symbol-prefix")]
    [InlineData("snapshot-outside-preserved-paths")]
    public void SnapshotPartitionGuardFailsOnEveryInventoryShapeMutation(string mutation)
    {
        var manifest = LoadManifest();

        switch (mutation)
        {
            case "duplicate-snapshot-path":
                manifest.SnapshotEntitySets[1].Path = manifest.SnapshotEntitySets[0].Path;
                break;
            case "duplicate-symbol-prefix":
                manifest.SnapshotEntitySets[1].SymbolPrefix = manifest.SnapshotEntitySets[0].SymbolPrefix;
                break;
            case "snapshot-outside-preserved-paths":
                manifest.SnapshotEntitySets[0].Path = "src/SignaCore.Host/Program.cs";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        Assert.ThrowsAny<XunitException>(() => AssertSnapshotInventoryIsPartitioned(manifest));
    }

    [Fact]
    public void PathOnlyCoverageCannotHideSymbolSensitiveBoundaries()
    {
        var manifest = LoadManifest();

        var adminConsole = Assert.Single(
            manifest.PreservedBoundaries,
            boundary => boundary.Id == "admin-console-product-ux");
        Assert.Equal("path-only", adminConsole.Coverage);
        Assert.Empty(adminConsole.PreservedSymbols);

        // Every other boundary is still required to carry explicit symbols.
        foreach (var boundary in manifest.PreservedBoundaries.Where(item => item.Id != adminConsole.Id))
        {
            Assert.Equal("path-and-symbol", boundary.Coverage);
            Assert.NotEmpty(boundary.PreservedSymbols);
        }
    }

    [Theory]
    [InlineData("unknown-coverage-mode")]
    [InlineData("path-only-with-symbols")]
    [InlineData("path-and-symbol-without-symbols")]
    public void BoundaryCoverageGuardFailsOnEveryCoverageMutation(string mutation)
    {
        var manifest = LoadManifest();
        var adminConsole = Assert.Single(
            manifest.PreservedBoundaries,
            boundary => boundary.Id == "admin-console-product-ux");
        var symbolBoundary = SnapshotBoundary(manifest);

        PreservedBoundary mutated;
        switch (mutation)
        {
            case "unknown-coverage-mode":
                mutated = symbolBoundary;
                mutated.Coverage = "symbol-only";
                break;
            case "path-only-with-symbols":
                mutated = adminConsole;
                mutated.PreservedSymbols.Add("SignaCore.Admin");
                break;
            case "path-and-symbol-without-symbols":
                mutated = symbolBoundary;
                mutated.PreservedSymbols.Clear();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        Assert.ThrowsAny<XunitException>(() => AssertBoundaryCoverage(mutated));
    }

    [Fact]
    public void ManifestSchemaRejectsMissingOrMisspelledSnapshotInventoryFields()
    {
        // A snapshot set that omits any of its four fields cannot be deserialized at all.
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "snapshotEntitySets": [{ "id": "postgresql-model-snapshot" }] }
            """));
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            {
              "snapshotEntitySets": [{
                "id": "postgresql-model-snapshot",
                "provider": "PostgreSQL",
                "path": "src/a.cs",
                "symbolPrefix_TYPO": "A.B",
                "entities": []
              }]
            }
            """));

        // The inventory itself and the boundary coverage mode are required members.
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "candidates": [], "preservedBoundaries": [] }
            """));
        Assert.Throws<JsonException>(() => DeserializeManifest("""
            { "preservedBoundaries": [{ "id": "x", "preservedSymbols": [] }] }
            """));
    }

    private static void AssertSnapshotInventoryIsPartitioned(MigrationManifest manifest)
    {
        var snapshots = manifest.SnapshotEntitySets;
        Assert.Equal(RequiredSnapshotEntitySets.Length, snapshots.Count);
        Assert.Equal(RequiredSnapshotEntitySets, snapshots.Select(snapshot => snapshot.Id).Order());
        AssertDistinct(snapshots.Select(snapshot => snapshot.Path));
        AssertDistinct(snapshots.Select(snapshot => snapshot.SymbolPrefix));
        AssertDistinct(snapshots.Select(snapshot => snapshot.Provider));

        var boundary = SnapshotBoundary(manifest);
        var retainedRoots = boundary.Paths.Select(NormalizePathRoot).ToArray();

        foreach (var snapshot in snapshots)
        {
            Assert.Matches(IdentifierPattern(), snapshot.Id);
            AssertRepositoryRelativePath(snapshot.Path);
            Assert.Matches(DottedIdentifierPattern(), snapshot.SymbolPrefix);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Provider));

            // The audited file must itself sit inside the retained migration paths; otherwise the
            // path gate, not the symbol gate, would already govern it.
            Assert.Contains(
                retainedRoots,
                retainedRoot => RootsOverlap(NormalizePathRoot(snapshot.Path), retainedRoot, '/'));

            Assert.Equal(EntitiesPerSnapshot, snapshot.Entities.Count);
            AssertDistinct(snapshot.Entities);
            Assert.All(snapshot.Entities, entity => Assert.Matches(EntityNamePattern(), entity));

            // Both snapshots must describe the same logical entity set.
            Assert.Equal(
                snapshots[0].Entities.Order(StringComparer.Ordinal),
                snapshot.Entities.Order(StringComparer.Ordinal));
        }

        var inventory = snapshots
            .SelectMany(snapshot => snapshot.Entities.Select(entity => $"{snapshot.SymbolPrefix}.{entity}"))
            .ToArray();
        Assert.Equal(RequiredSnapshotEntitySets.Length * EntitiesPerSnapshot, inventory.Length);
        AssertDistinct(inventory);

        var preserved = boundary.PreservedSymbols;
        AssertDistinct(preserved);
        Assert.All(preserved, symbol => Assert.True(IsSnapshotSymbol(manifest, symbol)));

        var deletion = DeletionSnapshotSymbols(manifest).ToArray();
        AssertDistinct(deletion);

        // Complete and mutually exclusive: every provider-specific symbol is claimed by exactly
        // one side, and no symbol is claimed by both.
        Assert.Empty(preserved.Intersect(deletion, StringComparer.Ordinal));
        Assert.Equal(
            inventory.Order(StringComparer.Ordinal),
            preserved.Union(deletion, StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // Snapshot convention symbols may only be claimed by the migration boundary or by a
        // deletion candidate; no unrelated product boundary may claim one by coincidence.
        foreach (var other in manifest.PreservedBoundaries.Where(item => item.Id != SnapshotBoundaryId))
        {
            foreach (var symbol in other.PreservedSymbols)
            {
                Assert.DoesNotContain(inventory, entry => RootsOverlap(entry, symbol, '.'));
            }
        }
    }

    private static void AssertBoundaryCoverage(PreservedBoundary boundary)
    {
        Assert.Contains(boundary.Coverage, BoundaryCoverageModes);
        Assert.NotEmpty(boundary.Paths);

        // path-only exists for non-C# boundaries. It must never be used to drop the symbol
        // requirement from a boundary that needs symbol-scoped deletion protection.
        if (boundary.Coverage == "path-only")
        {
            Assert.Empty(boundary.PreservedSymbols);
        }
        else
        {
            Assert.NotEmpty(boundary.PreservedSymbols);
        }
    }

    private static PreservedBoundary SnapshotBoundary(MigrationManifest manifest) =>
        Assert.Single(manifest.PreservedBoundaries, boundary => boundary.Id == SnapshotBoundaryId);

    private static bool IsSnapshotSymbol(MigrationManifest manifest, string symbol) =>
        manifest.SnapshotEntitySets.Any(snapshot =>
            symbol.StartsWith(snapshot.SymbolPrefix + ".", StringComparison.Ordinal));

    private static IEnumerable<string> DeletionSnapshotSymbols(MigrationManifest manifest) =>
        manifest.Candidates
            .SelectMany(candidate => candidate.LegacySymbols)
            .Where(symbol => IsSnapshotSymbol(manifest, symbol));

    private static void AssertDistinct(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        Assert.Equal(materialized.Length, materialized.Distinct(StringComparer.Ordinal).Count());
    }

    private static void AssertRepositoryRelativePath(string path)
    {
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(Path.IsPathRooted(path));
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.True(
            path.StartsWith("src/", StringComparison.Ordinal) ||
            path.StartsWith("tests/", StringComparison.Ordinal));
        var pathWithoutSupportedGlob = path.EndsWith("/**", StringComparison.Ordinal)
            ? path[..^3]
            : path;
        Assert.DoesNotContain('*', pathWithoutSupportedGlob);
        Assert.DoesNotContain('?', pathWithoutSupportedGlob);
        Assert.DoesNotContain('[', pathWithoutSupportedGlob);
        Assert.DoesNotContain(']', pathWithoutSupportedGlob);
    }

    private static string NormalizePathRoot(string path) =>
        path.EndsWith("/**", StringComparison.Ordinal) ? path[..^3] : path;

    private static void AssertImplementedReplacementExists(string reference)
    {
        var genericMarker = reference.IndexOf('<', StringComparison.Ordinal);
        var metadataName = genericMarker >= 0
            ? reference[..genericMarker] + "`1"
            : reference;
        var replacementAssemblies = new[]
        {
            typeof(ServiceMantle.Bootstrap.BootstrapFileStore).Assembly,
            typeof(ServiceMantle.Persistence.EntityFrameworkCore.EfCoreManagementAuditWriter<>).Assembly,
        };

        Assert.Contains(
            replacementAssemblies,
            assembly => assembly.GetType(metadataName, throwOnError: false, ignoreCase: false) is not null);
    }

    private static bool RootsOverlap(string first, string second, char separator) =>
        first.Equals(second, StringComparison.Ordinal) ||
        first.StartsWith(second + separator, StringComparison.Ordinal) ||
        second.StartsWith(first + separator, StringComparison.Ordinal);

    private static MigrationManifest LoadManifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var stream = File.OpenRead(Path.Combine(
            repositoryRoot,
            "docs",
            "signacore-legacy-migration",
            "manifest.json"));
        return DeserializeManifest(stream);
    }

    private static MigrationManifest DeserializeManifest(Stream stream) =>
        JsonSerializer.Deserialize<MigrationManifest>(stream, SerializerOptions()) ??
        throw new InvalidOperationException("The SignaCore legacy migration manifest is empty.");

    private static MigrationManifest DeserializeManifest(string json) =>
        JsonSerializer.Deserialize<MigrationManifest>(json, SerializerOptions()) ??
        throw new InvalidOperationException("The SignaCore legacy migration manifest is empty.");

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static string FindRepositoryRoot()
    {
        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ServiceMantle.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the ServiceMantle repository root.");
    }

    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^#[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueReferencePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.<>]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TestNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)+(?:<[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*>)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedIdentifierPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*(?:: [A-Za-z0-9_./, -]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySymbolPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityNamePattern();

    private sealed class MigrationManifest
    {
        public int SchemaVersion { get; init; }

        public SourceBaseline Source { get; init; } = new();

        public Dictionary<string, string> Commands { get; init; } = [];

        [JsonRequired]
        public List<SnapshotEntitySet> SnapshotEntitySets { get; init; } = [];

        public List<DeletionCandidate> Candidates { get; init; } = [];

        public List<PostDeletionAcceptance> PostDeletionAcceptance { get; init; } = [];

        public List<PreservedBoundary> PreservedBoundaries { get; init; } = [];

        public List<DeletionBatch> Batches { get; init; } = [];
    }

    private sealed class SourceBaseline
    {
        public string Repository { get; init; } = string.Empty;

        public string Commit { get; init; } = string.Empty;

        public string VerifiedCiRun { get; init; } = string.Empty;

        public string VerifiedCiConclusion { get; init; } = string.Empty;
    }

    private sealed class SnapshotEntitySet
    {
        [JsonRequired]
        public string Id { get; init; } = string.Empty;

        [JsonRequired]
        public string Provider { get; init; } = string.Empty;

        [JsonRequired]
        public string Path { get; set; } = string.Empty;

        [JsonRequired]
        public string SymbolPrefix { get; set; } = string.Empty;

        [JsonRequired]
        public List<string> Entities { get; init; } = [];
    }

    private sealed class DeletionCandidate
    {
        public string Id { get; init; } = string.Empty;

        public string Subsystem { get; init; } = string.Empty;

        public List<string> LegacyPaths { get; init; } = [];

        public List<string> LegacySymbols { get; init; } = [];

        public List<string> CallSites { get; init; } = [];

        public List<BehaviorEvidence> Evidence { get; init; } = [];

        public Replacement Replacement { get; init; } = new();

        public List<string> Prerequisites { get; init; } = [];

        [JsonRequired]
        public List<string> CoverageGaps { get; init; } = [];

        public string Disposition { get; init; } = string.Empty;

        public string Batch { get; init; } = string.Empty;
    }

    private sealed class BehaviorEvidence
    {
        public string Command { get; init; } = string.Empty;

        public string Test { get; init; } = string.Empty;

        public List<string> Scenarios { get; init; } = [];
    }

    private sealed class Replacement
    {
        public string Reference { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;
    }

    private sealed class PreservedBoundary
    {
        public string Id { get; init; } = string.Empty;

        public List<string> Paths { get; init; } = [];

        [JsonRequired]
        public string Coverage { get; set; } = string.Empty;

        [JsonRequired]
        public List<string> PreservedSymbols { get; init; } = [];

        public List<BoundaryTest> Tests { get; init; } = [];

        public string Rationale { get; init; } = string.Empty;
    }

    private sealed class BoundaryTest
    {
        public string Command { get; init; } = string.Empty;

        public string Test { get; init; } = string.Empty;
    }

    private sealed class PostDeletionAcceptance
    {
        public string Issue { get; init; } = string.Empty;

        public string AfterWorkstream { get; init; } = string.Empty;

        public List<string> Scenarios { get; init; } = [];
    }

    private sealed class DeletionBatch
    {
        public int Order { get; init; }

        public string Id { get; init; } = string.Empty;

        public string Subsystem { get; init; } = string.Empty;

        public List<string> CandidateIds { get; init; } = [];

        public List<string> Prerequisites { get; init; } = [];

        public int TrackingIssue { get; init; }

        public string ProposedTitle { get; init; } = string.Empty;
    }
}

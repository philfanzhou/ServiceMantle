using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

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

    private static readonly string[] RequiredPreservedBoundaries =
    [
        "account-credential-domain",
        "application-gateway-domain",
        "callback-domain",
        "ldap-domain",
        "oauth-jwt-token-domain",
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
            Assert.NotEmpty(candidate.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(candidate.Replacement.Reference));
            Assert.DoesNotContain(",", candidate.Replacement.Reference, StringComparison.Ordinal);
            Assert.Contains(candidate.Replacement.State, new[] { "implemented", "planned" });
            Assert.NotEmpty(candidate.Prerequisites);
            Assert.All(candidate.Prerequisites, prerequisite =>
                Assert.Matches(IssueReferencePattern(), prerequisite));
            Assert.Contains(candidate.Disposition, new[] { "blocked", "ready" });

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
        });

        var batchedCandidateIds = batches.SelectMany(batch => batch.CandidateIds).ToArray();
        Assert.Equal(
            manifest.Candidates.Select(candidate => candidate.Id).Order(),
            batchedCandidateIds.Order());
        Assert.Equal(batchedCandidateIds.Length, batchedCandidateIds.Distinct().Count());

        foreach (var candidate in manifest.Candidates)
        {
            var batch = Assert.Single(batches, batch => batch.Id == candidate.Batch);
            Assert.Contains(candidate.Id, batch.CandidateIds);
        }

        for (var index = 1; index < batches.Length; index++)
        {
            Assert.Contains($"#{batches[index - 1].TrackingIssue}", batches[index].Prerequisites);
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
        foreach (var boundary in manifest.PreservedBoundaries)
        {
            Assert.Matches(IdentifierPattern(), boundary.Id);
            Assert.NotEmpty(boundary.Paths);
            Assert.NotEmpty(boundary.Tests);
            Assert.False(string.IsNullOrWhiteSpace(boundary.Rationale));
            Assert.All(boundary.Paths, path =>
            {
                AssertRepositoryRelativePath(path);
                preservedRoots.Add(path.EndsWith("/**", StringComparison.Ordinal) ? path[..^3] : path);
            });
        }

        foreach (var legacyPath in manifest.Candidates.SelectMany(candidate => candidate.LegacyPaths))
        {
            Assert.DoesNotContain(preservedRoots, preservedRoot =>
                legacyPath.Equals(preservedRoot, StringComparison.Ordinal) ||
                legacyPath.StartsWith(preservedRoot + "/", StringComparison.Ordinal));
        }
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

    private static void AssertRepositoryRelativePath(string path)
    {
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(Path.IsPathRooted(path));
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.True(
            path.StartsWith("src/", StringComparison.Ordinal) ||
            path.StartsWith("tests/", StringComparison.Ordinal));
    }

    private static MigrationManifest LoadManifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var stream = File.OpenRead(Path.Combine(
            repositoryRoot,
            "docs",
            "signacore-legacy-migration",
            "manifest.json"));
        return JsonSerializer.Deserialize<MigrationManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("The SignaCore legacy migration manifest is empty.");
    }

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

    private sealed class MigrationManifest
    {
        public int SchemaVersion { get; init; }

        public SourceBaseline Source { get; init; } = new();

        public Dictionary<string, string> Commands { get; init; } = [];

        public List<DeletionCandidate> Candidates { get; init; } = [];

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

        public List<string> Tests { get; init; } = [];

        public string Rationale { get; init; } = string.Empty;
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

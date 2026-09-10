using System.Text.RegularExpressions;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

/// <summary>
/// Pins the gates that decide whether a run may reach NuGet.org.
/// </summary>
/// <remarks>
/// Publishing is irreversible, and the only place its preconditions are expressed is the workflow
/// file: a removed <c>needs</c> entry, a widened <c>if</c>, or an <c>id-token</c> grant that spreads
/// to another job would all be silent. Nothing else in the repository would notice, so the file
/// itself is the thing under test.
/// </remarks>
public sealed class ReleaseWorkflowTests
{
    private static readonly string Release = ReadWorkflow("release.yml");
    private static readonly string Ci = ReadWorkflow("ci.yml");

    [Fact]
    public void Release_runs_on_default_branch_pushes_and_on_release_tags()
    {
        Assert.Contains("branches: [main]", Release, StringComparison.Ordinal);
        Assert.Contains("\"v*.*.*\"", Release, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_tag_reaches_the_publishing_job()
    {
        var job = Job("publish-nuget");

        Assert.Contains(
            "if: ${{ needs.version.outputs.publish == 'true' }}",
            job,
            StringComparison.Ordinal);
        // resolve-version only ever answers publish=true for a tag; every other ref, including a
        // main push, resolves to an untagged version with publish=false.
        Assert.Contains("-- resolve-version", Job("version"), StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_requests_never_reach_a_publishing_path()
    {
        Assert.DoesNotContain("pull_request", Release, StringComparison.Ordinal);
        Assert.DoesNotContain("-- publish", Ci, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet/login", Ci, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_outside_main_is_refused_before_a_version_is_chosen()
    {
        var job = Job("version");

        Assert.Contains("fetch-depth: 0", job, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor", job, StringComparison.Ordinal);
        Assert.Contains("refs/heads/main:refs/remotes/origin/main", job, StringComparison.Ordinal);
        // The guard has to sit before the version is produced, or a rejected tag would still leave a
        // usable version output behind for a later job.
        Assert.True(
            job.IndexOf("git merge-base --is-ancestor", StringComparison.Ordinal) <
            job.IndexOf("-- resolve-version", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rejected_version_fails_the_step_instead_of_being_swallowed()
    {
        var job = Job("version");

        // Piping the tool's output into tee would hand the step tee's exit code, so a refused tag
        // would pass and leave an empty version behind. The output goes to a file instead.
        Assert.DoesNotContain("| tee", job, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", job, StringComparison.Ordinal);
        // A git tag may contain quote characters, so the ref name is never spliced into a command
        // line by expression interpolation.
        Assert.Contains("REF_NAME: ${{ github.ref_name }}", job, StringComparison.Ordinal);
        Assert.Contains("--ref-name \"$REF_NAME\"", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--ref-name \"${{", job, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_diagnostics_cannot_enter_the_version_output_file()
    {
        var job = Job("version");
        var build = job.IndexOf("- name: Build version resolver", StringComparison.Ordinal);
        var choose = job.IndexOf("- name: Choose version", StringComparison.Ordinal);

        Assert.True(build >= 0 && choose > build);
        var buildStep = job[build..choose];
        Assert.Contains("dotnet build", buildStep, StringComparison.Ordinal);
        Assert.Contains("eng/ServiceMantle.ReleaseTool/ServiceMantle.ReleaseTool.csproj", buildStep, StringComparison.Ordinal);
        Assert.Contains("--configuration Release", buildStep, StringComparison.Ordinal);
        Assert.DoesNotContain("$GITHUB_OUTPUT", buildStep, StringComparison.Ordinal);
        Assert.DoesNotContain("$RUNNER_TEMP/version.txt", buildStep, StringComparison.Ordinal);

        var chooseStep = job[choose..];
        Assert.Contains("dotnet run", chooseStep, StringComparison.Ordinal);
        Assert.Contains("--configuration Release", chooseStep, StringComparison.Ordinal);
        Assert.Contains("--no-build", chooseStep, StringComparison.Ordinal);
        Assert.Contains("--no-restore", chooseStep, StringComparison.Ordinal);
        Assert.Contains("-- resolve-version", chooseStep, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", chooseStep, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_rule_has_exactly_one_implementation()
    {
        // A second copy of the rule in shell is the failure this guards: it would drift from the
        // unit-tested one and decide a different version than the tests describe.
        Assert.DoesNotContain("^v[0-9]", Release, StringComparison.Ordinal);
        Assert.DoesNotContain("=~", Release, StringComparison.Ordinal);
        Assert.Contains("-- resolve-version", Release, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_is_unreachable_until_every_verification_gate_has_passed()
    {
        var job = Job("publish-nuget");

        foreach (var gate in new[] { "version", "verify", "bootstrap-credential-existence", "publish" })
        {
            Assert.Contains(gate, NeedsOf(job));
        }

        // GitHub does not start a job whose needs failed or were cancelled, and no condition here
        // overrides that, so an upstream failure or a cancelled run publishes nothing.
        Assert.DoesNotContain("always()", job, StringComparison.Ordinal);
        Assert.DoesNotContain("failure()", job, StringComparison.Ordinal);
    }

    [Fact]
    public void The_artifacts_that_are_pushed_are_the_artifacts_that_were_verified()
    {
        var job = Job("publish-nuget");

        Assert.Contains("actions/download-artifact@", job, StringComparison.Ordinal);
        Assert.Contains("name: package-${{ needs.version.outputs.number }}", job, StringComparison.Ordinal);
        Assert.DoesNotContain("-- pack", job, StringComparison.Ordinal);
        Assert.Contains("-- publish", job, StringComparison.Ordinal);
        Assert.Contains("--api-key-environment NUGET_API_KEY", job, StringComparison.Ordinal);
    }

    [Fact]
    public void The_publishing_credential_is_scoped_to_the_one_job_that_needs_it()
    {
        Assert.Contains("id-token: write", Job("publish-nuget"), StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(Release, @"id-token:\s*write", RegexOptions.None, TimeSpan.FromSeconds(5)));
        Assert.Single(
            Regex.Matches(Release, @"NuGet/login@", RegexOptions.None, TimeSpan.FromSeconds(5)));
        // The two jobs added for publishing run no git command, so the checkout token has no reason
        // to stay in the workspace beside the publishing credential.
        Assert.All(
            new[] { Job("publish-nuget"), Job("verify-published-packages") },
            job => Assert.Contains("persist-credentials: false", job, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_third_party_action_is_pinned_to_a_commit()
    {
        var references = Regex
            .Matches(Release, @"uses:\s*(?<reference>\S+)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["reference"].Value)
            .Where(reference => !reference.StartsWith("./", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.Matches("@[0-9a-f]{40}$", reference));
    }

    [Fact]
    public void A_published_version_is_proved_restorable_from_the_release_source()
    {
        var job = Job("verify-published-packages");

        Assert.Contains("publish-nuget", NeedsOf(job));
        Assert.Contains("published-package-consumption.sh", job, StringComparison.Ordinal);
        Assert.Contains("--version \"$PACKAGE_VERSION\"", job, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Workflow_jobs_are_found_with_either_checkout_line_ending(string newline)
    {
        const string workflow = "jobs:\n  version:\n    name: Decide version\n  publish:\n    name: Publish\n";
        var checkout = workflow.Replace("\n", newline, StringComparison.Ordinal);

        Assert.Equal("  version:\n    name: Decide version\n", Job("version", checkout));
        Assert.Equal("  publish:\n    name: Publish\n", Job("publish", checkout));
    }

    private static string[] NeedsOf(string job)
    {
        var match = Regex.Match(job, @"needs:\s*\[(?<list>[^\]]*)\]", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, "The job does not declare its prerequisites as a list.");
        return [.. match.Groups["list"].Value.Split(',').Select(entry => entry.Trim())];
    }

    private static string Job(string name, string? workflow = null)
    {
        // Windows checkouts may use CRLF. Normalize before matching and slicing so '$' sees the
        // same job headers and match offsets on every runner.
        var release = (workflow ?? Release).ReplaceLineEndings("\n");
        var jobs = Regex.Matches(
            release,
            @"^  (?<name>[A-Za-z][A-Za-z0-9-]*):$",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));
        for (var index = 0; index < jobs.Count; index++)
        {
            if (jobs[index].Groups["name"].Value != name)
            {
                continue;
            }

            var start = jobs[index].Index;
            var end = index + 1 < jobs.Count ? jobs[index + 1].Index : release.Length;
            return release[start..end];
        }

        throw new InvalidOperationException($"The release workflow declares no {name} job.");
    }

    private static string ReadWorkflow(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "eng", "packages.json")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            ".github",
            "workflows",
            name));
    }
}

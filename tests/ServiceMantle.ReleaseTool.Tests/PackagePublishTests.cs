using System.IO.Compression;
using System.Text;
using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class PackagePublishTests : IDisposable
{
    private const string Version = "0.1.0-rc.1";
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string OtherCommit = "2222222222222222222222222222222222222222";
    private const string Secret = "oy2-publish-credential-value";

    private static readonly string[] Identifiers = ["ServiceMantle", "ServiceMantle.AspNetCore"];

    private readonly string root = Directory
        .CreateTempSubdirectory("servicemantle-publish")
        .FullName;

    public PackagePublishTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        foreach (var id in Identifiers)
        {
            WritePackage(id, Commit, symbols: false);
            WritePackage(id, Commit, symbols: true);
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task An_empty_feed_receives_every_registered_package_and_its_symbols()
    {
        var feed = new StubFeed();
        var pusher = new StubPusher();
        var output = new StringWriter();

        var results = await PublishAsync(feed, pusher, output);

        Assert.All(results, result => Assert.Equal(PublishOutcome.Published, result.Outcome));
        Assert.Equal(
            [
                "ServiceMantle.0.1.0-rc.1.nupkg",
                "ServiceMantle.0.1.0-rc.1.snupkg",
                "ServiceMantle.AspNetCore.0.1.0-rc.1.nupkg",
                "ServiceMantle.AspNetCore.0.1.0-rc.1.snupkg",
            ],
            pusher.Pushed);
        Assert.Contains("published: 2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_already_published_from_this_commit_is_skipped_so_a_rerun_finishes()
    {
        var feed = new StubFeed();
        feed.Publish("ServiceMantle", Version, ReadPackage("ServiceMantle"));
        var pusher = new StubPusher();
        var output = new StringWriter();

        var results = await PublishAsync(feed, pusher, output);

        Assert.Equal(
            PublishOutcome.AlreadyPresent,
            results.Single(result => result.Id == "ServiceMantle").Outcome);
        Assert.Equal(
            PublishOutcome.Published,
            results.Single(result => result.Id == "ServiceMantle.AspNetCore").Outcome);
        Assert.Equal(
            [
                "ServiceMantle.0.1.0-rc.1.snupkg",
                "ServiceMantle.AspNetCore.0.1.0-rc.1.nupkg",
                "ServiceMantle.AspNetCore.0.1.0-rc.1.snupkg",
            ],
            pusher.Pushed);
    }

    [Fact]
    public async Task A_version_already_published_from_another_commit_fails_instead_of_being_skipped()
    {
        var feed = new StubFeed();
        feed.Publish("ServiceMantle", Version, BuildPackage("ServiceMantle", OtherCommit));
        var pusher = new StubPusher();
        var output = new StringWriter();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(feed, pusher, output));

        Assert.Contains("1 of 2", failure.Message, StringComparison.Ordinal);
        var report = output.ToString();
        Assert.Contains($"ServiceMantle {Version}", report, StringComparison.Ordinal);
        Assert.Contains(OtherCommit, report, StringComparison.Ordinal);
        // The rest of the set is still attempted, so one bad slot does not hide the feed's real state.
        Assert.Contains("ServiceMantle.AspNetCore.0.1.0-rc.1.nupkg", pusher.Pushed);
    }

    [Fact]
    public async Task A_published_package_without_origin_metadata_is_not_treated_as_ours()
    {
        var feed = new StubFeed();
        feed.Publish("ServiceMantle", Version, BuildPackage("ServiceMantle", commit: null));
        var output = new StringWriter();

        await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(feed, new StubPusher(), output));

        Assert.Contains("repository commit", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unauthorized", "credential")]
    [InlineData("failed", "could not be read")]
    public async Task A_feed_that_cannot_answer_fails_the_publish(string lookup, string expected)
    {
        var feed = new StubFeed
        {
            Fixed = new FeedResponse(
                lookup == "unauthorized" ? FeedLookup.Unauthorized : FeedLookup.Failed,
                null,
                "HTTP 401 Unauthorized"),
        };
        var pusher = new StubPusher();
        var output = new StringWriter();

        await Assert.ThrowsAsync<ReleaseToolException>(() => PublishAsync(feed, pusher, output));

        Assert.Empty(pusher.Pushed);
        var report = output.ToString();
        Assert.Contains(expected, report, StringComparison.Ordinal);
        // A feed that cannot answer - including one that ran out of time - is a per-package failure,
        // so the run still reaches its summary instead of ending as an unreported cancellation.
        Assert.Contains("failed: 2", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unauthorized", "credential")]
    [InlineData("failed", "push failed")]
    public async Task A_rejected_push_fails_the_publish(string status, string expected)
    {
        var pusher = new StubPusher
        {
            Fixed = new PushResponse(
                status == "unauthorized" ? PushStatus.Unauthorized : PushStatus.Failed,
                "HTTP 500 Internal Server Error"),
        };
        var output = new StringWriter();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(new StubFeed(), pusher, output));

        Assert.Contains("2 of 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_failure_reports_both_halves_rather_than_claiming_success()
    {
        var pusher = new StubPusher
        {
            FailFor = "ServiceMantle.AspNetCore.0.1.0-rc.1.nupkg",
        };
        var output = new StringWriter();

        await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(new StubFeed(), pusher, output));

        var report = output.ToString();
        Assert.Contains("published: 1", report, StringComparison.Ordinal);
        Assert.Contains("failed: 1", report, StringComparison.Ordinal);
        Assert.Contains($"ServiceMantle {Version}", report, StringComparison.Ordinal);
        Assert.Contains($"ServiceMantle.AspNetCore {Version}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_symbol_package_failure_is_reported_against_its_own_package()
    {
        var pusher = new StubPusher { FailFor = "ServiceMantle.0.1.0-rc.1.snupkg" };
        var output = new StringWriter();

        await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(new StubFeed(), pusher, output));

        Assert.Contains("symbol package push failed", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conflict_requires_read_back_and_reports_the_package_outcome(bool symbols)
    {
        var feed = new StubFeed();
        var pusher = new StubPusher
        {
            OnPush = (name, isSymbols) =>
            {
                if (name.StartsWith("ServiceMantle.0", StringComparison.Ordinal) && isSymbols == symbols)
                {
                    feed.Publish("ServiceMantle", Version, ReadPackage("ServiceMantle"));
                    return new PushResponse(PushStatus.AlreadyPresent, "HTTP 409");
                }

                return PushResponse.Succeeded;
            },
        };
        var output = new StringWriter();

        var results = await PublishAsync(feed, pusher, output);

        Assert.Equal(symbols ? PublishOutcome.Published : PublishOutcome.AlreadyPresent, results[0].Outcome);
        Assert.Equal(3, feed.LookupCount);
        Assert.Contains($"ServiceMantle.{Version}.snupkg", pusher.Pushed);
        Assert.Contains(symbols ? "published: 2" : "already present: 1", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "other")]
    [InlineData(true, "other")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "unauthorized")]
    [InlineData(true, "failed")]
    [InlineData(false, "no-commit")]
    [InlineData(true, "wrong-id")]
    [InlineData(false, "wrong-version")]
    public async Task An_unverified_conflict_fails_without_losing_other_package_results(bool symbols, string response)
    {
        var feed = new StubFeed();
        var pusher = new StubPusher
        {
            OnPush = (name, isSymbols) =>
            {
                if (!name.StartsWith("ServiceMantle.0", StringComparison.Ordinal) || isSymbols != symbols)
                {
                    return PushResponse.Succeeded;
                }

                feed.OnRead = id => id != "ServiceMantle" ? FeedResponse.Missing : response switch
                {
                    "missing" => FeedResponse.Missing,
                    "unauthorized" => new FeedResponse(FeedLookup.Unauthorized, null, "HTTP 401"),
                    "failed" => new FeedResponse(FeedLookup.Failed, null, "request timed out"),
                    _ => new FeedResponse(FeedLookup.Found,
                        BuildPackage(response == "wrong-id" ? "SomeoneElse" : "ServiceMantle",
                            response == "no-commit" ? null : response == "other" ? OtherCommit : Commit,
                            response == "wrong-version" ? "9.0.0" : Version), null),
                };
                return new PushResponse(PushStatus.AlreadyPresent, "HTTP 409");
            },
        };
        var output = new StringWriter();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => PublishAsync(feed, pusher, output));

        Assert.Equal(1, Program.ReportFailure(failure, TextWriter.Null));
        Assert.Contains("failed: 1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("published: 1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"ServiceMantle {Version}", output.ToString(), StringComparison.Ordinal);
        if (!symbols)
        {
            Assert.DoesNotContain($"ServiceMantle.{Version}.snupkg", pusher.Pushed);
        }
    }

    [Fact]
    public async Task A_rerun_retries_symbols_after_the_package_was_published()
    {
        var feed = new StubFeed();
        var pusher = new StubPusher
        {
            OnPush = (name, symbols) =>
            {
                if (name.StartsWith("ServiceMantle.0", StringComparison.Ordinal))
                {
                    if (symbols)
                    {
                        return new PushResponse(PushStatus.Failed, "HTTP 503");
                    }

                    feed.Publish("ServiceMantle", Version, ReadPackage("ServiceMantle"));
                }

                return PushResponse.Succeeded;
            },
        };
        await Assert.ThrowsAsync<ReleaseToolException>(() => PublishAsync(feed, pusher, new StringWriter()));
        var retry = new StubPusher();

        var results = await PublishAsync(feed, retry, new StringWriter());

        Assert.Equal(PublishOutcome.AlreadyPresent, results[0].Outcome);
        Assert.Contains($"ServiceMantle.{Version}.snupkg", retry.Pushed);
        Assert.DoesNotContain($"ServiceMantle.{Version}.nupkg", retry.Pushed);
    }

    [Theory]
    [InlineData("duplicate-nuspec")]
    [InlineData("duplicate-repository")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("invalid-zip")]
    public async Task Invalid_remote_metadata_is_a_package_failure(string shape)
    {
        var bytes = BuildPackage(shape == "wrong-id" ? "SomeoneElse" : "ServiceMantle", Commit,
            shape == "wrong-version" ? "9.0.0" : Version);
        if (shape.StartsWith("duplicate-", StringComparison.Ordinal))
        {
            using var buffer = new MemoryStream();
            buffer.Write(bytes);
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = archive.Entries.Single();
                string xml;
                using (var reader = new StreamReader(entry.Open()))
                {
                    xml = reader.ReadToEnd();
                }

                if (shape == "duplicate-repository")
                {
                    entry.Delete();
                    xml = xml.Replace("</metadata>", "<repository commit=\"ambiguous\" /></metadata>", StringComparison.Ordinal);
                }

                using var writer = new StreamWriter(archive.CreateEntry("extra.nuspec").Open());
                writer.Write(xml);
            }

            bytes = buffer.ToArray();
        }

        var feed = new StubFeed();
        feed.Publish("ServiceMantle", Version, shape == "invalid-zip" ? [1, 2, 3] : bytes);
        var output = new StringWriter();
        var pusher = new StubPusher();

        await Assert.ThrowsAsync<ReleaseToolException>(() => PublishAsync(feed, pusher, output));

        Assert.Contains("failed: 1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("published: 1", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain($"ServiceMantle.{Version}.snupkg", pusher.Pushed);
    }

    [Fact]
    public async Task Cancellation_during_conflict_read_back_stays_a_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var feed = new StubFeed();
        var pusher = new StubPusher
        {
            OnPush = (_, _) =>
            {
                feed.OnLookup = cancellation.Cancel;
                return new PushResponse(PushStatus.AlreadyPresent, "HTTP 409");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackagePublisher.PublishAsync(
            root, Registry(), Version, Commit, "packages", feed, pusher, false, new StringWriter(), cancellation.Token));
        Assert.Single(pusher.Pushed);
    }

    [Theory]
    [InlineData("nupkg", false)]
    [InlineData("snupkg", false)]
    [InlineData("nupkg", true)]
    [InlineData("snupkg", true)]
    [InlineData("all", false)]
    [InlineData("directory", true)]
    public async Task Missing_local_artifacts_are_identified_before_any_feed_access(string missing, bool dryRun)
    {
        var expected = new List<string>();
        foreach (var id in Identifiers)
        {
            foreach (var extension in new[] { "nupkg", "snupkg" })
            {
                if (missing is "all" or "directory" ||
                    (id == "ServiceMantle.AspNetCore" && extension == missing))
                {
                    File.Delete(Path.Combine(root, "packages", $"{id}.{Version}.{extension}"));
                    expected.Add($"{id} {Version}: missing .{extension} artifact");
                }
            }
        }

        if (missing == "directory")
        {
            Directory.Delete(Path.Combine(root, "packages"));
        }

        var feed = new StubFeed();
        var pusher = new StubPusher();
        var output = new StringWriter();
        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => PackagePublisher.PublishAsync(
            root, Registry(), Version, Commit, "packages", feed, pusher, dryRun, output,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, Program.ReportFailure(failure, output));
        Assert.All(expected, diagnostic => Assert.Contains(diagnostic, output.ToString(), StringComparison.Ordinal));
        Assert.Equal(0, feed.LookupCount);
        Assert.Empty(pusher.Pushed);
    }

    [Fact]
    public async Task A_version_the_feed_would_rewrite_fails_before_anything_is_pushed()
    {
        var pusher = new StubPusher();

        await Assert.ThrowsAsync<ReleaseToolException>(() => PackagePublisher.PublishAsync(
            root,
            Registry(),
            "0.1.0+build.5",
            Commit,
            "packages",
            new StubFeed(),
            pusher,
            dryRun: false,
            new StringWriter(),
            TestContext.Current.CancellationToken));

        Assert.Empty(pusher.Pushed);
    }

    [Fact]
    public async Task A_dry_run_compares_against_the_feed_but_pushes_nothing()
    {
        var feed = new StubFeed();
        feed.Publish("ServiceMantle", Version, BuildPackage("ServiceMantle", OtherCommit));
        var pusher = new StubPusher();
        var output = new StringWriter();

        await Assert.ThrowsAsync<ReleaseToolException>(() => PackagePublisher.PublishAsync(
            root,
            Registry(),
            Version,
            Commit,
            "packages",
            feed,
            pusher,
            dryRun: true,
            output,
            TestContext.Current.CancellationToken));

        Assert.Empty(pusher.Pushed);
        Assert.Equal(2, feed.LookupCount);
        Assert.Contains("dry run", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_the_publish_and_stays_a_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var feed = new StubFeed { OnLookup = cancellation.Cancel };
        var pusher = new StubPusher();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackagePublisher.PublishAsync(
            root,
            Registry(),
            Version,
            Commit,
            "packages",
            feed,
            pusher,
            dryRun: false,
            new StringWriter(),
            cancellation.Token));

        Assert.Equal(130, Program.ReportFailure(new OperationCanceledException(), TextWriter.Null));
    }

    [Fact]
    public async Task A_feed_diagnostic_carrying_the_credential_never_reaches_the_log()
    {
        var feed = new StubFeed
        {
            Fixed = new FeedResponse(FeedLookup.Failed, null, $"proxy rejected apiKey={Secret}"),
        };
        var buffer = new StringWriter();
        var output = new RedactingTextWriter(buffer, Secret);

        await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(feed, new StubPusher(), output));

        output.Flush();
        Assert.DoesNotContain(Secret, buffer.ToString(), StringComparison.Ordinal);
        Assert.Contains(RedactingTextWriter.Replacement, buffer.ToString(), StringComparison.Ordinal);
    }

    private Task<IReadOnlyList<PublishResult>> PublishAsync(
        IPackageFeed feed,
        IPackagePusher pusher,
        TextWriter output) =>
        PackagePublisher.PublishAsync(
            root,
            Registry(),
            Version,
            Commit,
            "packages",
            feed,
            pusher,
            dryRun: false,
            output,
            TestContext.Current.CancellationToken);

    private static PackageRegistry Registry() => new()
    {
        SchemaVersion = 1,
        Packages = [.. Identifiers.Select(id => new RegisteredPackage
        {
            Id = id,
            Project = $"src/{id}/{id}.csproj",
        })],
    };

    private byte[] ReadPackage(string id) =>
        File.ReadAllBytes(Path.Combine(root, "packages", $"{id}.{Version}.nupkg"));

    private void WritePackage(string id, string commit, bool symbols)
    {
        var extension = symbols ? "snupkg" : "nupkg";
        File.WriteAllBytes(
            Path.Combine(root, "packages", $"{id}.{Version}.{extension}"),
            BuildPackage(id, commit));
    }

    private static byte[] BuildPackage(string id, string? commit, string version = Version)
    {
        var repository = commit is null
            ? "<repository type=\"git\" url=\"https://github.com/philfanzhou/ServiceMantle\" />"
            : $"<repository type=\"git\" url=\"https://github.com/philfanzhou/ServiceMantle\" commit=\"{commit}\" />";
        var nuspec =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<package><metadata>" +
            $"<id>{id}</id><version>{version}</version>" +
            "<license type=\"expression\">MIT</license>" +
            repository +
            "</metadata></package>";

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{id}.nuspec");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(nuspec));
        }

        return buffer.ToArray();
    }

    private sealed class StubFeed : IPackageFeed
    {
        private readonly Dictionary<string, byte[]> published = new(StringComparer.Ordinal);

        internal FeedResponse? Fixed { get; set; }

        internal Func<string, FeedResponse>? OnRead { get; set; }

        internal Action? OnLookup { get; set; }

        internal int LookupCount { get; private set; }

        internal void Publish(string id, string version, byte[] content) =>
            published[$"{id}/{version}"] = content;

        public Task<FeedResponse> TryGetPackageAsync(
            string id,
            string version,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            OnLookup?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (OnRead is not null)
            {
                return Task.FromResult(OnRead(id));
            }

            if (Fixed is not null)
            {
                return Task.FromResult(Fixed);
            }

            return Task.FromResult(published.TryGetValue($"{id}/{version}", out var content)
                ? new FeedResponse(FeedLookup.Found, content, null)
                : FeedResponse.Missing);
        }
    }

    private sealed class StubPusher : IPackagePusher
    {
        internal List<string> Pushed { get; } = [];

        internal PushResponse? Fixed { get; set; }

        internal Func<string, bool, PushResponse>? OnPush { get; set; }

        internal string? FailFor { get; set; }

        public Task<PushResponse> PushAsync(
            string filePath,
            bool symbols,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(filePath);
            Assert.Equal(symbols, name.EndsWith(".snupkg", StringComparison.Ordinal));
            if (Fixed is not null)
            {
                return Task.FromResult(Fixed);
            }

            if (string.Equals(name, FailFor, StringComparison.Ordinal))
            {
                return Task.FromResult(new PushResponse(PushStatus.Failed, "HTTP 503 Service Unavailable"));
            }

            Pushed.Add(name);
            return Task.FromResult(OnPush?.Invoke(name, symbols) ?? PushResponse.Succeeded);
        }
    }
}

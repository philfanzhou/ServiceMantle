using System.Net;
using System.Text;
using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class NuGetV3SourceTests : IDisposable
{
    private const string SourceUrl = "https://feed.example/v3/index.json";
    private const string BaseAddress = "https://feed.example/v3-flatcontainer/";
    private const string PublishAddress = "https://feed.example/api/v2/package";
    private const string SymbolAddress = "https://feed.example/api/v2/symbolpackage";
    private const string ApiKey = "oy2-publish-credential-value";

    private const string Index = $$"""
        {
          "resources": [
            { "@type": "PackageBaseAddress/3.0.0", "@id": "{{BaseAddress}}" },
            { "@type": "PackagePublish/2.0.0", "@id": "{{PublishAddress}}" },
            { "@type": "SymbolPackagePublish/4.9.0", "@id": "{{SymbolAddress}}" }
          ]
        }
        """;

    private readonly string directory = Directory
        .CreateTempSubdirectory("servicemantle-source")
        .FullName;

    public void Dispose()
    {
        foreach (var source in sources)
        {
            source.Dispose();
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task A_package_the_feed_does_not_have_is_reported_missing_from_its_lowercase_address()
    {
        var handler = new StubHandler(Index);
        handler.Respond(
            $"{BaseAddress}servicemantle.aspnetcore/0.1.0-rc.1/servicemantle.aspnetcore.0.1.0-rc.1.nupkg",
            HttpStatusCode.NotFound);
        var source = Create(handler);

        var response = await source.TryGetPackageAsync(
            "ServiceMantle.AspNetCore",
            "0.1.0-RC.1",
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedLookup.Missing, response.Lookup);
    }

    [Fact]
    public async Task A_package_the_feed_has_comes_back_with_its_bytes()
    {
        var handler = new StubHandler(Index);
        handler.Respond(
            $"{BaseAddress}servicemantle/0.1.0/servicemantle.0.1.0.nupkg",
            HttpStatusCode.OK,
            "package-bytes");
        var source = Create(handler);

        var response = await source.TryGetPackageAsync(
            "ServiceMantle",
            "0.1.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedLookup.Found, response.Lookup);
        Assert.Equal("package-bytes", Encoding.UTF8.GetString(response.Content!));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "Unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, "Failed")]
    [InlineData(HttpStatusCode.BadGateway, "Failed")]
    public async Task A_feed_error_is_classified_by_its_status_code(
        HttpStatusCode status,
        string expected)
    {
        var handler = new StubHandler(Index);
        handler.Respond($"{BaseAddress}servicemantle/0.1.0/servicemantle.0.1.0.nupkg", status);
        var source = Create(handler);

        var response = await source.TryGetPackageAsync(
            "ServiceMantle",
            "0.1.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.Lookup.ToString());
        Assert.Contains(((int)status).ToString(), response.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_index_fails_the_lookup_rather_than_reporting_the_package_missing()
    {
        var handler = new StubHandler(index: null);
        var source = Create(handler);

        var response = await source.TryGetPackageAsync(
            "ServiceMantle",
            "0.1.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedLookup.Failed, response.Lookup);
    }

    [Fact]
    public async Task A_push_sends_the_credential_as_a_header_and_uses_the_signal_specific_endpoint()
    {
        var handler = new StubHandler(Index);
        handler.Respond(PublishAddress, HttpStatusCode.Created);
        handler.Respond(SymbolAddress, HttpStatusCode.Accepted);
        var source = Create(handler);

        Assert.Equal(
            PushStatus.Succeeded,
            (await source.PushAsync(
                WriteArtifact("ServiceMantle.0.1.0.nupkg"),
                symbols: false,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            PushStatus.Succeeded,
            (await source.PushAsync(
                WriteArtifact("ServiceMantle.0.1.0.snupkg"),
                symbols: true,
                TestContext.Current.CancellationToken)).Status);

        Assert.Equal([PublishAddress, SymbolAddress], handler.PushTargets);
        Assert.Equal([ApiKey, ApiKey], handler.PushCredentials);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "Unauthorized")]
    [InlineData(HttpStatusCode.Conflict, "AlreadyPresent")]
    [InlineData(HttpStatusCode.InternalServerError, "Failed")]
    public async Task A_rejected_push_is_classified_by_its_status_code(
        HttpStatusCode status,
        string expected)
    {
        var handler = new StubHandler(Index);
        handler.Respond(PublishAddress, status);
        var source = Create(handler);

        var response = await source.PushAsync(
            WriteArtifact("ServiceMantle.0.1.0.nupkg"),
            symbols: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.Status.ToString());
    }

    [Fact]
    public async Task A_push_without_a_credential_is_refused_before_the_artifact_leaves_the_machine()
    {
        var handler = new StubHandler(Index);
        handler.Respond(PublishAddress, HttpStatusCode.Created);
        var source = new NuGetV3Source(new HttpClient(handler), SourceUrl, apiKey: null);

        var response = await source.PushAsync(
            WriteArtifact("ServiceMantle.0.1.0.nupkg"),
            symbols: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(PushStatus.Unauthorized, response.Status);
        Assert.Empty(handler.PushTargets);
    }

    [Fact]
    public async Task A_missing_artifact_fails_the_push()
    {
        var handler = new StubHandler(Index);
        var source = Create(handler);

        var response = await source.PushAsync(
            Path.Combine(directory, "absent.nupkg"),
            symbols: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(PushStatus.Failed, response.Status);
        Assert.Empty(handler.PushTargets);
    }

    [Fact]
    public async Task A_source_without_the_needed_resource_fails_rather_than_guessing_an_address()
    {
        var handler = new StubHandler("""{ "resources": [] }""");
        var source = Create(handler);

        Assert.Equal(
            FeedLookup.Failed,
            (await source.TryGetPackageAsync("ServiceMantle", "0.1.0", TestContext.Current.CancellationToken))
                .Lookup);
        Assert.Equal(
            PushStatus.Failed,
            (await source.PushAsync(
                WriteArtifact("ServiceMantle.0.1.0.nupkg"),
                symbols: false,
                TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task The_service_index_is_read_once_however_many_packages_are_handled()
    {
        var handler = new StubHandler(Index);
        handler.Respond($"{BaseAddress}a/0.1.0/a.0.1.0.nupkg", HttpStatusCode.NotFound);
        handler.Respond($"{BaseAddress}b/0.1.0/b.0.1.0.nupkg", HttpStatusCode.NotFound);
        var source = Create(handler);

        await source.TryGetPackageAsync("a", "0.1.0", TestContext.Current.CancellationToken);
        await source.TryGetPackageAsync("b", "0.1.0", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.IndexReads);
    }

    private readonly List<NuGetV3Source> sources = [];

    private NuGetV3Source Create(StubHandler handler)
    {
        var source = new NuGetV3Source(new HttpClient(handler), SourceUrl, ApiKey);
        sources.Add(source);
        return source;
    }

    private string WriteArtifact(string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "artifact");
        return path;
    }

    private sealed class StubHandler(string? index) : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> responses =
            new(StringComparer.Ordinal);

        internal List<string> PushTargets { get; } = [];

        internal List<string?> PushCredentials { get; } = [];

        internal int IndexReads { get; private set; }

        internal void Respond(string address, HttpStatusCode status, string body = "") =>
            responses[address] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var address = request.RequestUri!.ToString();
            if (address == SourceUrl)
            {
                IndexReads++;
                return Task.FromResult(index is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index) });
            }

            if (request.Method == HttpMethod.Put)
            {
                PushTargets.Add(address);
                PushCredentials.Add(request.Headers.TryGetValues("X-NuGet-ApiKey", out var values)
                    ? values.Single()
                    : null);
            }

            return Task.FromResult(responses.TryGetValue(address, out var response)
                ? new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotImplemented));
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ServiceMantle.ReleaseTool;

/// <summary>
/// Reads and writes a NuGet v3 feed over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Talking to the feed directly rather than shelling out to <c>dotnet nuget push</c> is what makes
/// the failure contract expressible: the exit code of the CLI is 1 for a rejected credential, a
/// server error, and a duplicate alike, so a caller that has to treat those three differently would
/// be left parsing English error text. Here the status code is the answer.
/// </para>
/// <para>
/// It also keeps the credential out of the process table: it travels in a request header instead of
/// a child process's argument list.
/// </para>
/// </remarks>
internal sealed class NuGetV3Source(
    HttpClient client,
    string sourceUrl,
    string? apiKey) : IPackageFeed, IPackagePusher, IDisposable
{
    private const string PackageBaseAddressType = "PackageBaseAddress/3.0.0";
    private const string PackagePublishType = "PackagePublish/2.0.0";
    private const string SymbolPackagePublishType = "SymbolPackagePublish/4.9.0";
    private const string ApiKeyHeader = "X-NuGet-ApiKey";

    private readonly SemaphoreSlim indexGate = new(1, 1);
    private IReadOnlyDictionary<string, string>? resources;

    public async Task<FeedResponse> TryGetPackageAsync(
        string id,
        string version,
        CancellationToken cancellationToken)
    {
        string baseAddress;
        try
        {
            baseAddress = await ResolveResourceAsync(PackageBaseAddressType, cancellationToken);
        }
        catch (ReleaseToolException exception)
        {
            return new FeedResponse(FeedLookup.Failed, null, exception.Message);
        }

        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var address = $"{baseAddress.TrimEnd('/')}/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

        try
        {
            using var response = await client.GetAsync(address, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => FeedResponse.Missing,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new FeedResponse(FeedLookup.Unauthorized, null, Describe(response)),
                _ when response.IsSuccessStatusCode => new FeedResponse(
                    FeedLookup.Found,
                    await response.Content.ReadAsByteArrayAsync(cancellationToken),
                    null),
                _ => new FeedResponse(FeedLookup.Failed, null, Describe(response)),
            };
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            return new FeedResponse(FeedLookup.Failed, null, Describe(exception));
        }
    }

    public async Task<PushResponse> PushAsync(
        string filePath,
        bool symbols,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new PushResponse(PushStatus.Failed, $"{Path.GetFileName(filePath)} does not exist");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return new PushResponse(PushStatus.Unauthorized, "no publish credential was supplied");
        }

        string endpoint;
        try
        {
            endpoint = await ResolveResourceAsync(
                symbols ? SymbolPackagePublishType : PackagePublishType,
                cancellationToken);
        }
        catch (ReleaseToolException exception)
        {
            return new PushResponse(PushStatus.Failed, exception.Message);
        }

        try
        {
            await using var file = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            var part = new StreamContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "package", Path.GetFileName(filePath));

            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = content };
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
            // NuGet.org requires this protocol declaration from third-party push clients.
            // X-NuGet-Client-Version is reserved for the official NuGet client.
            // https://learn.microsoft.com/en-us/nuget/api/nuget-protocols
            request.Headers.Add("X-NuGet-Protocol-Version", "4.1.0");
            using var response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new PushResponse(PushStatus.Unauthorized, Describe(response)),
                HttpStatusCode.Conflict => new PushResponse(PushStatus.AlreadyPresent, Describe(response)),
                _ when response.IsSuccessStatusCode => PushResponse.Succeeded,
                _ => new PushResponse(PushStatus.Failed, Describe(response)),
            };
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            return new PushResponse(PushStatus.Failed, Describe(exception));
        }
    }

    private async Task<string> ResolveResourceAsync(string type, CancellationToken cancellationToken)
    {
        await indexGate.WaitAsync(cancellationToken);
        try
        {
            resources ??= await LoadIndexAsync(cancellationToken);
        }
        finally
        {
            indexGate.Release();
        }

        return resources.TryGetValue(type, out var address)
            ? address
            : throw new ReleaseToolException($"The release source does not offer a {type} resource.");
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadIndexAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(sourceUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseToolException(
                    $"The release source index could not be read ({Describe(response)}).");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in document.RootElement.GetProperty("resources").EnumerateArray())
            {
                var type = resource.TryGetProperty("@type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var address = resource.TryGetProperty("@id", out var addressElement)
                    ? addressElement.GetString()
                    : null;
                if (type is { Length: > 0 } && address is { Length: > 0 })
                {
                    found.TryAdd(type, address);
                }
            }

            return found;
        }
        catch (Exception exception) when (
            IsTransportFailure(exception, cancellationToken) || exception is JsonException or KeyNotFoundException)
        {
            throw new ReleaseToolException(
                $"The release source index could not be read ({Describe(exception)}).");
        }
    }

    public void Dispose() => indexGate.Dispose();

    /// <summary>
    /// Decides whether a thrown exception is the feed failing rather than the caller stopping.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient"/> reports its own timeout as a cancellation, so the exception type
    /// alone cannot separate "the maintainer interrupted the release" from "the feed took longer
    /// than the timeout allows". The caller's token is what distinguishes them, and the distinction
    /// is load-bearing: a real cancellation has to stay a cancellation and reach the caller as one,
    /// while a timeout is a network failure that has to be reported against the package it belongs
    /// to and counted among the failures in the summary.
    /// </remarks>
    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or IOException => true,
            _ => false,
        };

    private string Describe(Exception exception) =>
        exception is OperationCanceledException
            ? $"the request did not complete within {client.Timeout}"
            : exception.Message;

    private static string Describe(HttpResponseMessage response) =>
        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ServiceMantle.AspNetCore.ManagementApi.Session;
using ServiceMantle.Management;

namespace ServiceMantle.ReferenceService.Management;

/// <summary>
/// The sample's login adapter: it strictly parses the <c>{ "username", "secret" }</c> JSON envelope
/// inside the body ServiceMantle already admitted and hands the credentials to the scoped accessor
/// and the identity provider.
/// </summary>
/// <remarks>
/// <para>
/// The adapter writes nothing to the response, retains no credential, and signs nothing in. A
/// malformed envelope, a wrong media type, or an in-envelope schema violation answers
/// <see cref="ManagementIdentityResult.Unauthenticated"/> without calling the provider at all, so
/// no credential material is matched and nothing about the directory is leaked.
/// </para>
/// <para>
/// Names are matched exactly and after unescaping: a differently cased or escaped duplicate of a
/// known member is a schema violation, and so is every unknown member. The username and the secret
/// are never trimmed or normalized. The admitted body is read exactly once, as one of the two
/// alternative readers the shared contract offers.
/// </para>
/// </remarks>
public static class ReferenceManagementLoginAdapter
{
    private const string UsernameName = "username";
    private const string SecretName = "secret";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    /// <summary>The adapter handed to <c>MapServiceMantleManagementSession</c>.</summary>
    public static async ValueTask<ManagementIdentityResult> AdaptAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsJsonContentType(httpContext.Request.ContentType))
        {
            return ManagementIdentityResult.Unauthenticated();
        }

        var credentials = await TryParseEnvelopeAsync(httpContext, cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            // A request that never carried a parseable envelope is not a login attempt: the
            // provider is not called and nothing is compared against the directory.
            return ManagementIdentityResult.Unauthenticated();
        }

        httpContext.RequestServices.GetRequiredService<ReferenceOperatorCredentialAccessor>()
            .Set(credentials.Value.username, credentials.Value.secret);
        return await ManagementIdentityProviderInvoker.InvokeAsync(
            httpContext.RequestServices.GetRequiredService<IManagementIdentityProvider>(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string username, string secret)?> TryParseEnvelopeAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? username = null;
            string? secret = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    return null;
                }

                switch (property.Name)
                {
                    case UsernameName:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        username = property.Value.GetString();
                        break;
                    case SecretName:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        secret = property.Value.GetString();
                        break;
                    default:
                        return null;
                }
            }

            return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret)
                ? null
                : (username, secret);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parsed.Parameters.Count == 0)
        {
            return true;
        }

        return parsed.Parameters.Count == 1 &&
            string.Equals(parsed.Parameters[0].Name.Value, "charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                HeaderUtilities.RemoveQuotes(parsed.Parameters[0].Value).Value,
                "utf-8",
                StringComparison.OrdinalIgnoreCase);
    }
}

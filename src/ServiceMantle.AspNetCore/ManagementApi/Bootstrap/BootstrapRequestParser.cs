using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.AspNetCore.ManagementApi.Bootstrap;

/// <summary>
/// The strictly parsed body of one Bootstrap management request.
/// </summary>
/// <remarks>
/// A property that was absent is null here. The distinction between "absent" and "explicitly null"
/// is settled inside the parser, so a caller of this type never has to reconstruct it: an explicit
/// null is rejected before a request object is produced.
/// </remarks>
internal sealed class BootstrapRequest
{
    internal BootstrapRequest(
        BootstrapDatabaseConfiguration? database,
        string? masterKey)
    {
        Database = database;
        MasterKey = masterKey;
    }

    /// <summary>Gets the complete database replacement, or null when the property was absent.</summary>
    internal BootstrapDatabaseConfiguration? Database { get; }

    /// <summary>Gets the master key, or null when the property was absent.</summary>
    internal string? MasterKey { get; }

    /// <summary>Returns a projection that never contains a connection string or a master key.</summary>
    public override string ToString() =>
        $"BootstrapRequest(DatabaseProvided={Database is not null}, " +
        $"MasterKeyProvided={MasterKey is not null})";
}

/// <summary>
/// Parses the two properties the Bootstrap management entries accept.
/// </summary>
/// <remarks>
/// <para>
/// This group owns its own strict parser, so it states the shared admission rules again rather than
/// sharing a mutable helper with another endpoint's limits. Nothing it reads is echoed: a rejection
/// is a bare <see langword="null"/>, and no connection string, master key, provider, or server
/// version reaches a message, a log, or an exception.
/// </para>
/// <para>
/// The wire shape is lower camel case and is independent of the PascalCase file on disk. Names are
/// matched exactly and after unescaping, so a differently cased or escaped duplicate is a duplicate.
/// </para>
/// </remarks>
internal static class BootstrapRequestParser
{
    private const string DatabaseName = "database";
    private const string MasterKeyName = "masterKey";
    private const string ProviderName = "provider";
    private const string ConnectionStringName = "connectionString";
    private const string ServerVersionName = "serverVersion";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = BootstrapMapping.MaximumJsonDepth,
    };

    /// <summary>
    /// Reads and parses the request body, or returns null for every unusable shape.
    /// </summary>
    /// <param name="context">The current management request.</param>
    /// <param name="requireBoth">
    /// True for the creation entry, where both properties must be present; false for the update
    /// entry, where at least one must be.
    /// </param>
    internal static async ValueTask<BootstrapRequest?> ParseAsync(
        HttpContext context,
        bool requireBoth)
    {
        var request = context.Request;
        if (request.QueryString.HasValue ||
            request.Headers.ContainsKey(HeaderNames.ContentEncoding) ||
            !IsJsonContentType(request.ContentType) ||
            request.ContentLength is > BootstrapMapping.MaximumBodyLength)
        {
            return null;
        }

        var body = new ArrayBufferWriter<byte>();
        var rented = ArrayPool<byte>.Shared.Rent(BootstrapMapping.MaximumBodyLength);
        try
        {
            while (true)
            {
                var remaining = BootstrapMapping.MaximumBodyLength - body.WrittenCount;
                var read = await request.Body
                    .ReadAsync(
                        rented.AsMemory(0, Math.Min(rented.Length, remaining + 1)),
                        context.RequestAborted)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > remaining)
                {
                    // One byte past the limit is enough to prove the body is oversized, whether or
                    // not a Content-Length declared it.
                    return null;
                }

                body.Write(rented.AsSpan(0, read));
            }

            return Parse(body.WrittenMemory, requireBoth);
        }
        finally
        {
            // The rented buffer held the connection string and the master key; it is cleared before
            // it returns to the pool.
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static BootstrapRequest? Parse(ReadOnlyMemory<byte> body, bool requireBoth)
    {
        // A byte order mark is not part of a JSON document on the wire and is refused rather than
        // skipped.
        if (body.Length >= 3 &&
            body.Span[0] == 0xEF && body.Span[1] == 0xBB && body.Span[2] == 0xBF)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            JsonElement database = default;
            JsonElement masterKey = default;
            var databasePresent = false;
            var masterKeyPresent = false;
            foreach (var property in root.EnumerateObject())
            {
                // Names are compared after unescaping, so an escaped repeat of the same name is a
                // duplicate rather than a second field.
                if (!seen.Add(property.Name))
                {
                    return null;
                }

                switch (property.Name)
                {
                    case DatabaseName:
                        database = property.Value;
                        databasePresent = true;
                        break;
                    case MasterKeyName:
                        masterKey = property.Value;
                        masterKeyPresent = true;
                        break;
                    default:
                        // Unknown, differently cased, and disk-only names are all refused.
                        return null;
                }
            }

            if (requireBoth
                ? !databasePresent || !masterKeyPresent
                : !databasePresent && !masterKeyPresent)
            {
                return null;
            }

            BootstrapDatabaseConfiguration? parsedDatabase = null;
            if (databasePresent)
            {
                parsedDatabase = ParseDatabase(database);
                if (parsedDatabase is null)
                {
                    return null;
                }
            }

            string? parsedMasterKey = null;
            if (masterKeyPresent)
            {
                // An explicit null and a blank value are both refused, so "retain the old value" is
                // never expressed by a value that reached the wire.
                if (masterKey.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                parsedMasterKey = masterKey.GetString();
                if (string.IsNullOrWhiteSpace(parsedMasterKey))
                {
                    return null;
                }
            }

            return new BootstrapRequest(parsedDatabase, parsedMasterKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BootstrapDatabaseConfiguration? ParseDatabase(JsonElement database)
    {
        if (database.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? provider = null;
        string? connectionString = null;
        string? serverVersion = null;
        var serverVersionPresent = false;
        foreach (var property in database.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return null;
            }

            switch (property.Name)
            {
                case ProviderName:
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    provider = property.Value.GetString();
                    break;
                case ConnectionStringName:
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    connectionString = property.Value.GetString();
                    break;
                case ServerVersionName:
                    serverVersionPresent = true;
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    serverVersion = property.Value.GetString();
                    break;
                default:
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(provider) ||
            string.IsNullOrWhiteSpace(connectionString) ||
            (serverVersionPresent && serverVersion is not null &&
                string.IsNullOrWhiteSpace(serverVersion)) ||
            // The provider shape is the core rule, applied here so an unusable id is a request
            // rejection rather than a validator failure.
            !DatabaseProviderId.TryNormalize(provider, out _))
        {
            return null;
        }

        try
        {
            return new BootstrapDatabaseConfiguration(provider, serverVersion, connectionString);
        }
        catch (ArgumentException)
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

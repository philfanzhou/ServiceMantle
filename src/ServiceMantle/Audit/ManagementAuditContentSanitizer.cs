using System.Text.RegularExpressions;

namespace ServiceMantle.Audit;

/// <summary>
/// Applies the sensitive-content policy for audit descriptions and structured metadata:
/// metadata keys that name a secret are rejected outright, and free-text values are scanned for
/// secret-shaped substrings (connection strings, bearer tokens, JWTs, PEM key blocks) that are
/// redacted in place so audit content can never carry connection strings, root keys, database
/// administrator credentials, setup codes, passwords, tokens, or other sensitive configuration values.
/// </summary>
internal static partial class ManagementAuditContentSanitizer
{
    private static readonly string[] BlockedMetadataKeyTokens =
    [
        "password", "pwd", "secret", "token", "apikey", "api_key", "api-key",
        "connectionstring", "connection_string", "connstr", "credential",
        "privatekey", "private_key", "rootkey", "root_key", "masterkey", "master_key",
        "setupcode", "setup_code", "clientsecret", "client_secret",
        "accesskey", "access_key", "authorization", "cookie"
    ];

    internal static void EnsureMetadataKeyAllowed(string key)
    {
        var normalizedKey = key.ToLowerInvariant();
        foreach (var blockedToken in BlockedMetadataKeyTokens)
        {
            if (normalizedKey.Contains(blockedToken, StringComparison.Ordinal))
            {
                throw new ManagementAuditException(
                    "audit.metadata_key_rejected",
                    "The audit metadata key names a sensitive value and is not allowed.");
            }
        }
    }

    internal static string Redact(string value)
    {
        var redacted = KeyValueSecretPattern().Replace(value, match => $"{match.Groups["key"].Value}=[REDACTED]");
        redacted = BearerTokenPattern().Replace(redacted, "Bearer [REDACTED]");
        redacted = JwtLikePattern().Replace(redacted, "[REDACTED_TOKEN]");
        redacted = PemBlockPattern().Replace(redacted, "[REDACTED_KEY]");
        return redacted;
    }

    [GeneratedRegex(
        @"(?<key>(password|pwd|secret|token|apikey|api_key|masterkey|master_key))\s*=\s*[^;\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_.~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(
        @"[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtLikePattern();

    [GeneratedRegex(
        @"-----BEGIN[^-]*PRIVATE KEY-----[\s\S]*?-----END[^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemBlockPattern();
}

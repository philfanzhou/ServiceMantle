using System.Text;
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
        "password", "pwd", "secret", "token", "apikey",
        "connectionstring", "connstr", "credential", "privatekey", "rootkey", "masterkey",
        "setupcode", "clientsecret", "accesskey", "authorization", "cookie"
    ];

    internal static void EnsureMetadataKeyAllowed(string key)
    {
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey.Length == 0)
        {
            throw new ManagementAuditException(
                "audit.metadata_key_rejected",
                "The audit metadata key could not be normalized safely.");
        }

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
        var redacted = DatabaseUriPattern().Replace(value, "[REDACTED]");
        redacted = ConnectionStringPattern().Replace(redacted, "[REDACTED]");
        redacted = SensitiveKeyValuePattern().Replace(
            redacted,
            match => $"{match.Groups["key"].Value}{match.Groups["separator"].Value}[REDACTED]");
        redacted = BearerTokenPattern().Replace(redacted, "Bearer [REDACTED]");
        redacted = JwtLikePattern().Replace(redacted, "[REDACTED_TOKEN]");
        redacted = PemBlockPattern().Replace(redacted, "[REDACTED_KEY]");
        return redacted;
    }

    internal static void EnsureNoSensitiveContent(string value, string errorCode, string fieldDescription)
    {
        if (!string.Equals(value, Redact(value), StringComparison.Ordinal))
        {
            throw new ManagementAuditException(
                errorCode,
                $"The audit {fieldDescription} contains sensitive content and is not allowed.");
        }
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is >= 'A' and <= 'Z')
            {
                builder.Append((char)(character + ('a' - 'A')));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
            }
            else if (character > 0x7f)
            {
                throw new ManagementAuditException(
                    "audit.metadata_key_rejected",
                    "The audit metadata key contains characters that cannot be checked safely.");
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(
        @"(?<key>pass(?:[\s_-]*word)?|pwd|secret|token|api[\s_-]*key|master[\s_-]*key|root[\s_-]*key|setup[\s_-]*code|connection[\s_-]*(?:string|str)|client[\s_-]*secret|access[\s_-]*key|credential|authorization|cookie)(?<separator>(?:\\?[""'])?\s*(?::|=|\bis\b)\s*)(?:\\?[""'][^}\]\r\n]*?\\?[""']|[^;,\r\n}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyValuePattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:host|server|data[\s_-]*source|address)\s*=\s*[^;\r\n]+(?:;\s*[A-Za-z][A-Za-z0-9 _-]*\s*=\s*[^;\r\n]*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:postgres(?:ql)?|mysql|mariadb|sqlserver):\/\/[^\s,;]+|(?<![A-Za-z0-9])(?:[A-Za-z][A-Za-z0-9+.-]*):\/\/[^\s\/?#@]+@[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseUriPattern();

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

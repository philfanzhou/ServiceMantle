using System.Collections.Frozen;
using System.Globalization;

namespace ServiceMantle.Logging;

/// <summary>
/// Owns immutable structured field and Header name policy snapshots and classification.
/// </summary>
internal sealed class StructuredLogNamePolicy
{
    private static readonly string[] BuiltInDeniedFieldFragments =
    [
        "password",
        "passwd",
        "passphrase",
        "pwd",
        "secret",
        "token",
        "apikey",
        "connectionstring",
        "connstr",
        "credential",
        "privatekey",
        "rootkey",
        "masterkey",
        "setupcode",
        "clientsecret",
        "accesskey",
        "accountkey",
        "authorization",
        "cookie"
    ];

    private static readonly string[] BuiltInDeniedHeaders =
    [
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "x-auth-token"
    ];

    private readonly FrozenSet<string> allowedFields;
    private readonly FrozenSet<string> deniedFieldFragments;
    private readonly FrozenSet<string> allowedHeaders;
    private readonly FrozenSet<string> deniedHeaders;
    private readonly bool allowUnlistedFields;
    private readonly bool allowUnlistedHeaders;

    internal StructuredLogNamePolicy(StructuredLogSanitizerOptions options)
    {
        allowedFields = MaterializeFieldNames(options.AllowedFieldNames, []);
        deniedFieldFragments = MaterializeFieldNames(
            options.DeniedFieldNames,
            BuiltInDeniedFieldFragments);
        allowedHeaders = MaterializeHeaderNames(options.AllowedHeaderNames, []);
        deniedHeaders = MaterializeHeaderNames(
            options.DeniedHeaderNames,
            BuiltInDeniedHeaders);
        allowUnlistedFields = options.AllowUnlistedFields;
        allowUnlistedHeaders = options.AllowUnlistedHeaders;
    }

    internal bool TryClassifyField(
        string name,
        out string outputName,
        out bool denied,
        out bool allowed)
    {
        if (!TryNormalizeFieldName(name, out outputName, out var policyName))
        {
            denied = true;
            allowed = false;
            return false;
        }

        denied = deniedFieldFragments.Any(fragment =>
            policyName.Contains(fragment, StringComparison.Ordinal));
        allowed = denied || allowUnlistedFields || allowedFields.Contains(policyName);
        return true;
    }

    internal bool IsDeniedField(string name) =>
        TryClassifyField(name, out _, out var denied, out _) && denied;

    internal bool TryGetFieldName(object? key, out string fieldName)
    {
        switch (key)
        {
            case string text:
                fieldName = text;
                return true;
            case Guid guid:
                fieldName = guid.ToString("D");
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                fieldName = Convert.ToString(key, CultureInfo.InvariantCulture)!;
                return true;
            case Enum enumeration:
                fieldName = StructuredLogSafeScalar.NormalizeEnum(enumeration)
                    .ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                fieldName = string.Empty;
                return false;
        }
    }

    internal bool TryClassifyHeader(
        string name,
        out string outputName,
        out bool denied,
        out bool allowed)
    {
        outputName = string.Empty;
        if (!TryNormalizeHeaderName(name, out var normalizedName))
        {
            denied = true;
            allowed = false;
            return false;
        }

        outputName = name.Trim();
        denied = deniedHeaders.Contains(normalizedName);
        allowed = denied || allowUnlistedHeaders || allowedHeaders.Contains(normalizedName);
        return true;
    }

    private static bool TryNormalizeFieldName(
        string? name,
        out string outputName,
        out string policyName)
    {
        outputName = string.Empty;
        policyName = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (candidate.Length > 128)
        {
            return false;
        }

        Span<char> policyBuffer = stackalloc char[candidate.Length];
        var policyLength = 0;
        foreach (var character in candidate)
        {
            if (character is >= 'A' and <= 'Z')
            {
                policyBuffer[policyLength++] = (char)(character + ('a' - 'A'));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                policyBuffer[policyLength++] = character;
            }
            else if (character is not ('.' or '_' or '-' or '@' or ':' or '/' or '[' or ']'))
            {
                return false;
            }
        }

        if (policyLength == 0)
        {
            return false;
        }

        outputName = candidate;
        policyName = new string(policyBuffer[..policyLength]);
        return true;
    }

    private static string NormalizeConfiguredFieldName(string name)
    {
        if (!TryNormalizeFieldName(name, out _, out var policyName))
        {
            throw new ArgumentException("A structured log field policy name is invalid.");
        }

        return policyName;
    }

    private static bool TryNormalizeHeaderName(string? name, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (candidate.Length > 128)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[candidate.Length];
        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (character is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(character + ('a' - 'A'));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                buffer[index] = character;
            }
            else
            {
                return false;
            }
        }

        normalizedName = new string(buffer);
        return true;
    }

    private static FrozenSet<string> MaterializeFieldNames(
        IEnumerable<string> configured,
        IEnumerable<string> builtIn)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configured);
            return builtIn
                .Concat(configured)
                .Select(NormalizeConfiguredFieldName)
                .ToFrozenSet(StringComparer.Ordinal);
        }
        catch
        {
            throw new ArgumentException("Structured log field policy options are invalid.");
        }
    }

    private static FrozenSet<string> MaterializeHeaderNames(
        IEnumerable<string> configured,
        IEnumerable<string> builtIn)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configured);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in builtIn.Concat(configured))
            {
                if (!TryNormalizeHeaderName(name, out var normalizedName))
                {
                    throw new ArgumentException();
                }

                names.Add(normalizedName);
            }

            return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            throw new ArgumentException("Structured log Header policy options are invalid.");
        }
    }
}

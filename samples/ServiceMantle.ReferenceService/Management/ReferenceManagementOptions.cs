using System.Security.Cryptography;
using System.Text;
using ServiceMantle.Management;

namespace ServiceMantle.ReferenceService.Management;

/// <summary>
/// The deployment-provided operator directory and management root key of the sample service.
/// </summary>
/// <remarks>
/// <para>
/// This is a sample-purpose external identity source: the directory entries are deployment facts
/// supplied through configuration, never accounts the service creates, stores, or manages. The
/// sample invents no network authentication protocol and provisions no local administrator.
/// </para>
/// <para>
/// Every input is explicit and is read before <c>Build</c>. The root key must be present and long
/// enough when the management session is wired: a missing or short key fails startup instead of
/// degrading, because a process-local random fallback would invalidate every existing cookie after
/// a restart and make cross-instance sharing accidental. A failure names the setting, never the
/// value it read.
/// </para>
/// </remarks>
public sealed class ReferenceManagementOptions
{
    /// <summary>The management root key protecting the shared Data Protection key ring.</summary>
    public const string RootKeySetting = "ReferenceService:Management:RootKey";

    /// <summary>The configuration section holding the operator directory.</summary>
    public const string OperatorsSection = "ReferenceService:Management:Operators";

    /// <summary>The smallest accepted root key length, in characters.</summary>
    public const int MinimumRootKeyLength = 32;

    private ReferenceManagementOptions(string rootKey, IReadOnlyList<ReferenceOperatorDirectoryEntry> operators)
    {
        RootKey = rootKey;
        Operators = operators;
    }

    /// <summary>Gets the root key. It never reaches a log line, a response, or an exception.</summary>
    public string RootKey { get; }

    /// <summary>
    /// Gets the operator directory, or an empty list when the deployment supplied none. An empty
    /// directory leaves login always failing with the fixed not-configured classification.
    /// </summary>
    public IReadOnlyList<ReferenceOperatorDirectoryEntry> Operators { get; }

    /// <summary>
    /// Reads the explicit inputs. The root key is required; the operator directory is optional.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// The root key is missing or too short, or the operator directory is malformed. The message
    /// names the setting or section only, never a configured value.
    /// </exception>
    public static ReferenceManagementOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rootKey = configuration[RootKeySetting];
        if (string.IsNullOrWhiteSpace(rootKey) || rootKey.Trim().Length < MinimumRootKeyLength)
        {
            throw new InvalidOperationException(
                $"The reference management session requires an explicit '{RootKeySetting}' of at " +
                $"least {MinimumRootKeyLength} characters. The configured value is not echoed.");
        }

        var entries = configuration.GetSection(OperatorsSection)
            .Get<List<ReferenceOperatorEntry>>() ?? [];
        var operators = new List<ReferenceOperatorDirectoryEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Id) ||
                string.IsNullOrWhiteSpace(entry.Credential) ||
                !seenIds.Add(entry.Id.Trim()))
            {
                throw InvalidDirectory();
            }

            var permissions = ParsePermissions(entry.Permissions);
            if (permissions.Count == 0)
            {
                throw InvalidDirectory();
            }

            operators.Add(new ReferenceOperatorDirectoryEntry(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.DisplayName) ? null : entry.DisplayName.Trim(),
                permissions,
                entry.Credential));
        }

        return new ReferenceManagementOptions(rootKey.Trim(), operators.AsReadOnly());
    }

    private static List<ManagementPermission> ParsePermissions(string? permissions)
    {
        var parsed = new List<ManagementPermission>();
        if (permissions is null)
        {
            return parsed;
        }

        foreach (var value in permissions.Split(',', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries))
        {
            if (!ManagementPermissions.TryParse(value, out var permission))
            {
                return [];
            }

            parsed.Add(permission);
        }

        return parsed;
    }

    private static InvalidOperationException InvalidDirectory() => new(
        $"Every entry of '{OperatorsSection}' needs a unique non-blank id, a non-blank credential, " +
        "and at least one known permission name. No configured value is echoed.");
}

/// <summary>The bound shape of one configured operator directory entry.</summary>
public sealed class ReferenceOperatorEntry
{
    /// <summary>The operator identifier presented as the login username.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>An optional display name for the session identity.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// A comma-separated list of permission names, for example
    /// <c>management.read,management.admin</c>.
    /// </summary>
    public string Permissions { get; set; } = string.Empty;

    /// <summary>The shared secret presented as the login credential.</summary>
    public string Credential { get; set; } = string.Empty;
}

/// <summary>
/// One immutable operator directory entry. The credential lives only between the scoped credential
/// accessor and the identity provider; it never reaches a log, a response, or an exception.
/// </summary>
public sealed class ReferenceOperatorDirectoryEntry(
    string id,
    string? displayName,
    IReadOnlyList<ManagementPermission> permissions,
    string credential)
{
    /// <summary>Gets the operator identifier.</summary>
    public string Id { get; } = id;

    /// <summary>Gets the display name, or null when the deployment supplied none.</summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>Gets the granted permissions.</summary>
    public IReadOnlyList<ManagementPermission> Permissions { get; } = permissions;

    /// <summary>Gets the shared secret.</summary>
    internal string Credential { get; } = credential;

    /// <summary>
    /// Computes the fixed-length digest used for the fixed-time comparison of one presented value
    /// against this entry's stored value.
    /// </summary>
    internal static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

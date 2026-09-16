using System.Security.Cryptography;
using ServiceMantle.Audit;
using ServiceMantle.Management;

namespace ServiceMantle.ReferenceService.Management;

/// <summary>
/// The sample's external management identity provider: it resolves one login against the
/// deployment-provided operator directory and never creates, stores, or manages any account.
/// </summary>
/// <remarks>
/// <para>
/// The operator directory is an external deployment fact, the same way an LDAP directory or an
/// operator database would be. With no configured directory the provider keeps answering the fixed
/// <c>reference.external_identity_not_configured</c> failure, so login is impossible rather than
/// accidentally open.
/// </para>
/// <para>
/// Both the username and the credential are compared in fixed time. Each side is reduced to a
/// fixed-length SHA-256 digest first, so the comparison never ends early on a length difference,
/// and the same single comparison implementation serves every directory entry.
/// </para>
/// </remarks>
public sealed class ReferenceExternalManagementIdentityProvider(
    ReferenceManagementOptions options,
    ReferenceOperatorCredentialAccessor accessor) : IManagementIdentityProvider
{
    private const string NotConfiguredCode = "reference.external_identity_not_configured";

    /// <inheritdoc />
    public ValueTask<ManagementIdentityResult> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Operators.Count == 0)
        {
            return ValueTask.FromResult(ManagementIdentityResult.Failed(NotConfiguredCode));
        }

        if (!accessor.TryRead(out var username, out var secret))
        {
            return ValueTask.FromResult(ManagementIdentityResult.Unauthenticated());
        }

        var usernameDigest = ReferenceOperatorDirectoryEntry.Digest(username);
        var secretDigest = ReferenceOperatorDirectoryEntry.Digest(secret);
        ReferenceOperatorDirectoryEntry? match = null;
        var ambiguity = false;
        foreach (var entry in options.Operators)
        {
            // Both comparisons are over fixed-length digests, so a non-matching length cannot end
            // the comparison early and no entry learns more than the final boolean.
            var usernameMatches = CryptographicOperations.FixedTimeEquals(
                usernameDigest,
                ReferenceOperatorDirectoryEntry.Digest(entry.Id));
            var secretMatches = CryptographicOperations.FixedTimeEquals(
                secretDigest,
                ReferenceOperatorDirectoryEntry.Digest(entry.Credential));
            if (usernameMatches && secretMatches)
            {
                if (match is not null)
                {
                    ambiguity = true;
                    break;
                }

                match = entry;
            }
        }

        if (match is null || ambiguity)
        {
            // A wrong credential and an unknown operator are the same answer.
            return ValueTask.FromResult(ManagementIdentityResult.Unauthenticated());
        }

        return ValueTask.FromResult(ManagementIdentityResult.Authenticated(ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            match.Id,
            match.Permissions,
            match.DisplayName)));
    }
}

namespace ServiceMantle.Audit;

/// <summary>
/// Structural comparison helpers for audit metadata dictionaries. Audit types expose metadata as
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, for which the default equality comparer falls
/// back to reference comparison; these helpers give the owning types consistent value semantics.
/// </summary>
internal static class AuditMetadataEquality
{
    /// <summary>
    /// Compares two metadata dictionaries entry by entry, ignoring enumeration order.
    /// </summary>
    public static bool AreEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes a hash code for metadata that is independent of enumeration order, so dictionaries
    /// that compare equal under <see cref="AreEqual"/> always hash alike.
    /// </summary>
    public static int GetHashCode(IReadOnlyDictionary<string, string> metadata)
    {
        var hash = 0;
        foreach (var (key, value) in metadata)
        {
            hash ^= HashCode.Combine(key, value);
        }

        return hash;
    }
}

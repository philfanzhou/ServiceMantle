namespace ServiceMantle.Migration;

/// <summary>Explicit additional comparison dimensions. Existing dimensions always remain enabled.</summary>
public sealed class SchemaEvidenceComparisonOptions
{
    /// <summary>Initializes immutable comparison options. New dimensions default to disabled.</summary>
    public SchemaEvidenceComparisonOptions(bool compareObjectNames = false, bool compareIndexKeyDetails = false)
    {
        CompareObjectNames = compareObjectNames;
        CompareIndexKeyDetails = compareIndexKeyDetails;
    }
    /// <summary>Gets whether catalog object names are compared using ordinal equality.</summary>
    public bool CompareObjectNames { get; }
    /// <summary>Gets whether total key counts and included columns are compared.</summary>
    public bool CompareIndexKeyDetails { get; }
}

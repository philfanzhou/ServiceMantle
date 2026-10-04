using Microsoft.EntityFrameworkCore.Metadata;

namespace ServiceMantle.Persistence.Relational.Migration;

/// <summary>Explicitly enables relational object names and caller-provided INCLUDE evidence.</summary>
public sealed class EfCoreExpectedSchemaDerivationOptions
{
    /// <summary>Creates immutable derivation options. The resolver returns store column names, or null.</summary>
    public EfCoreExpectedSchemaDerivationOptions(bool includeExtendedObjectEvidence = false,
        Func<ITableIndex, IReadOnlyList<string>?>? includeColumnResolver = null)
    {
        IncludeExtendedObjectEvidence = includeExtendedObjectEvidence;
        IncludeColumnResolver = includeColumnResolver;
    }

    /// <summary>Whether relational object names and index details are included.</summary>
    public bool IncludeExtendedObjectEvidence { get; }

    /// <summary>The caller's INCLUDE resolver, invoked only in extended mode.</summary>
    public Func<ITableIndex, IReadOnlyList<string>?>? IncludeColumnResolver { get; }
}

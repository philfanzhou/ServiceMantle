namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>Explicitly selects extended evidence and an exact schema/table scope.</summary>
public sealed class PostgreSqlSchemaEvidenceReadOptions
{
    /// <summary>Creates immutable options. Null scope selects all tables; empty selects none.</summary>
    public PostgreSqlSchemaEvidenceReadOptions(bool includeExtendedObjectEvidence = false,
        IReadOnlyList<(string Schema, string Table)>? tables = null)
    {
        IncludeExtendedObjectEvidence = includeExtendedObjectEvidence;
        if (tables is null) return;
        foreach (var (schema, table) in tables)
            if (!Valid(schema) || !Valid(table))
                throw new ArgumentException("Table identifiers must contain 1 to 128 visible characters and no controls.", nameof(tables));
        Tables = Array.AsReadOnly(tables.Distinct().ToArray());
    }

    /// <summary>Whether names, expression key counts and INCLUDE columns are read.</summary>
    public bool IncludeExtendedObjectEvidence { get; }

    /// <summary>The copied exact pairs, or null for all candidate tables.</summary>
    public IReadOnlyList<(string Schema, string Table)>? Tables { get; }

    private static bool Valid(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}

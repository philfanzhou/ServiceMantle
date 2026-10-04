using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

public sealed class SchemaEvidenceCatalogTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Enabled_dimensions_are_independent_and_preserve_full_payloads(bool names, bool details)
    {
        var expectedIndex = new SchemaIndex(["id"], false, "ix_expected", 2, ["note"]);
        var actualIndex = new SchemaIndex(["id"], false, "ix_actual", 1, []);
        var expectedKey = new SchemaPrimaryKey(["id"], "pk_expected");
        var actualKey = new SchemaPrimaryKey(["id"], "pk_actual");
        var expectedForeignKey = ForeignKey("fk_expected");
        var actualForeignKey = ForeignKey("fk_actual");
        var expected = new ExpectedSchema([Table([expectedIndex], [expectedForeignKey], expectedKey)]);
        var actual = new SchemaSnapshot([Table([actualIndex], [actualForeignKey], actualKey)]);
        Assert.Empty(SchemaEvidenceComparer.Compare(actual, expected));
        var result = SchemaEvidenceComparer.Compare(actual, expected, new(names, details));
        Assert.Equal((names ? 3 : 0) + (details ? 2 : 0), result.Count);
        Assert.Equal(names ? 3 : 0, result.Count(d => d.Kind == SchemaDifferenceKind.NameMismatch));
        if (names)
        {
            Assert.Contains(result, d => ReferenceEquals(d.ExpectedPrimaryKey, expectedKey) && ReferenceEquals(d.ActualPrimaryKey, actualKey));
            Assert.Contains(result, d => ReferenceEquals(d.ExpectedForeignKey, expectedForeignKey) && ReferenceEquals(d.ActualForeignKey, actualForeignKey));
        }
        foreach (var difference in result.Where(d => d.ExpectedIndex is not null))
        {
            Assert.Same(expectedIndex, difference.ExpectedIndex);
            Assert.Same(actualIndex, difference.ActualIndex);
        }
    }

    [Fact]
    public void Named_identity_and_unnamed_shape_identity_have_separate_domains()
    {
        Assert.Equal(3, Table([new(["id"], false), new(["id"], false, "a", 1, []), new(["id"], false, "b", 1, [])]).Indexes.Count);
        Assert.Equal(3, Table([], [ForeignKey(null), ForeignKey("a"), ForeignKey("b")]).ForeignKeys.Count);
        Assert.Throws<ArgumentException>(() => Table([new(["id"], false, "a", 1, []), new(["note"], true, "a", 1, [])]));
        Assert.Throws<ArgumentException>(() => Table([], [ForeignKey("a"), ForeignKey("a", "note")]));
        Assert.Throws<ArgumentException>(() => Table([new(["id"], false), new(["id"], false)]));
        Assert.Throws<ArgumentException>(() => Table([], [ForeignKey(null), ForeignKey(null)]));
    }

    [Fact]
    public void Same_name_is_reserved_before_fallback_and_changed_shape_is_reported()
    {
        var expected = new ExpectedSchema([Table([new(["id"], false, "rename", 1, []), new(["note"], false, "stable", 1, [])])]);
        var actual = new SchemaSnapshot([Table([new(["id"], false, "stable", 1, []), new(["id"], false, "other", 1, [])])]);
        var result = SchemaEvidenceComparer.Compare(actual, expected, new(true, true));
        Assert.Equal(2, result.Count);
        Assert.Equal(SchemaDifferenceKind.NameMismatch, result[0].Kind);
        Assert.Equal("other", result[0].ActualIndex!.Name);
        Assert.Equal(SchemaDifferenceKind.IndexMismatch, result[1].Kind);
        Assert.Equal("stable", result[1].ExpectedIndex!.Name);
        Assert.Equal("stable", result[1].ActualIndex!.Name);
    }

    [Fact]
    public void Multisets_keep_duplicates_extras_and_input_order_in_both_modes()
    {
        var first = new SchemaIndex(["id"], false, "a", 1, []);
        var second = new SchemaIndex(["id"], false, "b", 1, []);
        var extra = new SchemaIndex(["id"], false, "c", 1, []);
        foreach (var options in new[] { new SchemaEvidenceComparisonOptions(), new(true, true) })
        {
            Assert.Empty(SchemaEvidenceComparer.Compare(new([Table([second, first])]), new([Table([first, second])]), options));
            var differences = SchemaEvidenceComparer.Compare(new([Table([second, first, extra])]), new([Table([first, second])]), options);
            Assert.Equal(options.CompareObjectNames ? 2 : 1, differences.Count);
            Assert.Same(extra, differences[0].ActualIndex);
            Assert.Null(differences[0].ExpectedIndex);
        }
    }

    [Fact]
    public void Unknown_names_are_not_treated_as_known_and_name_plus_shape_changes_keep_both_facts()
    {
        var result = SchemaEvidenceComparer.Compare(new([Table([new(["id"], false)])]),
            new([Table([new(["id"], false, "named", 1, [])])]), new(true));
        Assert.Equal(SchemaDifferenceKind.NameMismatch, Assert.Single(result).Kind);
        result = SchemaEvidenceComparer.Compare(new([Table([new(["note"], true, "actual", 1, [])])]),
            new([Table([new(["id"], false, "expected", 1, [])])]), new(true));
        Assert.Equal(2, result.Count(d => d.Kind == SchemaDifferenceKind.IndexMismatch));
        Assert.Equal(2, result.Count(d => d.Kind == SchemaDifferenceKind.NameMismatch));
        var primaryKeyResult = SchemaEvidenceComparer.Compare(new([Table([], primaryKey: new(["note"], "actual"))]),
            new([Table([], primaryKey: new(["id"], "expected"))]), new(true));
        Assert.Equal(new[] { SchemaDifferenceKind.PrimaryKeyMismatch, SchemaDifferenceKind.NameMismatch }, primaryKeyResult.Select(d => d.Kind));
    }

    [Fact]
    public void Expressions_includes_and_names_are_validated_and_immutable()
    {
        var keys = new List<string> { "id" };
        var includes = new List<string> { "note" };
        var mixed = new SchemaIndex(keys, false, "index", 2, includes);
        keys.Clear(); includes.Clear();
        Assert.True(mixed.HasExpressionKeys);
        Assert.Equal(["id"], mixed.Columns);
        Assert.Equal(["note"], mixed.IncludedColumns);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)mixed.IncludedColumns).Clear());
        Assert.True(new SchemaIndex([], false, null, 1, []).HasExpressionKeys);
        Assert.False(new SchemaIndex(["id"], false).HasExpressionKeys);
        Assert.Throws<ArgumentException>(() => new SchemaIndex([], false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaIndex([], false, null, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaIndex(["id", "note"], false, null, 1, []));
        foreach (var name in new[] { "", " ", "invalid\0", new string('a', 129) })
        {
            Assert.Throws<ArgumentException>(() => new SchemaPrimaryKey(["id"], name));
            Assert.Throws<ArgumentException>(() => ForeignKey(name));
            Assert.Throws<ArgumentException>(() => new SchemaIndex(["id"], false, name, 1, []));
            Assert.Throws<ArgumentException>(() => new SchemaIndex([], false, null, 1, [name]));
        }
        Assert.Throws<ArgumentNullException>(() => SchemaEvidenceComparer.Compare(new([]), new([]), null!));
        Assert.Equal(9, (int)SchemaDifferenceKind.IndexMismatch);
    }

    private static SchemaForeignKey ForeignKey(string? name, string column = "id") =>
        new([column], "parents", ["id"], SchemaForeignKeyDeleteRule.Cascade, null, name);
    private static SchemaTable Table(IReadOnlyList<SchemaIndex> indexes,
        IReadOnlyList<SchemaForeignKey>? foreignKeys = null, SchemaPrimaryKey? primaryKey = null) =>
        new("table", [new("id", "integer", false), new("note", "text", true)], primaryKey, foreignKeys, indexes);
}

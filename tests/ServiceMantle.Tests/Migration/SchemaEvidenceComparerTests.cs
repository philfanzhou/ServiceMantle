using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Unit tests for <see cref="SchemaEvidenceComparer"/>: every difference kind, the
/// all-differences guarantee, the closed comparison dimensions, and the deterministic order.
/// </summary>
public sealed class SchemaEvidenceComparerTests
{
    [Fact]
    public void Identical_schemas_produce_no_differences()
    {
        var snapshot = Snapshot(Table("orders", [Column("id", "integer", false, SchemaIdentityKind.Always)]));
        var expected = Expected(Table("orders", [Column("id", "integer", false, SchemaIdentityKind.Always)]));

        var differences = SchemaEvidenceComparer.Compare(snapshot, expected);

        Assert.Empty(differences);
    }

    [Fact]
    public void A_missing_table_reports_the_expected_table()
    {
        var snapshot = Snapshot();
        var expectedTable = Table("orders", [Column("id", "integer", false)]);
        var expected = Expected(expectedTable);

        var difference = Assert.Single(SchemaEvidenceComparer.Compare(snapshot, expected));

        Assert.Equal(SchemaDifferenceKind.MissingTable, difference.Kind);
        Assert.Equal("orders", difference.TableName);
        Assert.Null(difference.TableSchema);
        Assert.Same(expectedTable, difference.ExpectedTable);
        Assert.Null(difference.ActualTable);
    }

    [Fact]
    public void An_extra_table_reports_the_actual_table()
    {
        var actualTable = Table("legacy_notes", [Column("id", "integer", false)]);
        var snapshot = Snapshot(
            Table("orders", [Column("id", "integer", false)]),
            actualTable);
        var expected = Expected(Table("orders", [Column("id", "integer", false)]));

        var difference = Assert.Single(SchemaEvidenceComparer.Compare(snapshot, expected));

        Assert.Equal(SchemaDifferenceKind.ExtraTable, difference.Kind);
        Assert.Equal("legacy_notes", difference.TableName);
        Assert.Same(actualTable, difference.ActualTable);
        Assert.Null(difference.ExpectedTable);
    }

    [Fact]
    public void Tables_correlate_by_schema_qualified_name()
    {
        var snapshot = Snapshot(Table("orders", [Column("id", "integer", false)], schema: "archive"));
        var expected = Expected(Table("orders", [Column("id", "integer", false)]));

        var differences = SchemaEvidenceComparer.Compare(snapshot, expected);

        Assert.Equal(2, differences.Count);
        Assert.Equal(SchemaDifferenceKind.MissingTable, differences[0].Kind);
        Assert.Equal(SchemaDifferenceKind.ExtraTable, differences[1].Kind);
        Assert.Equal("archive", differences[1].TableSchema);
    }

    [Fact]
    public void A_missing_column_carries_the_backfill_evidence()
    {
        var snapshot = Snapshot(Table(
            "orders",
            [Column("id", "integer", false)]));
        var expected = Expected(Table(
            "orders",
            [
                Column("id", "integer", false),
                Column("note", "text", true, hasStoredDefault: true),
            ]));

        var difference = Assert.Single(SchemaEvidenceComparer.Compare(snapshot, expected));

        Assert.Equal(SchemaDifferenceKind.MissingColumn, difference.Kind);
        Assert.Equal("orders", difference.TableName);
        Assert.Equal("note", difference.ColumnName);
        Assert.NotNull(difference.ExpectedColumn);
        Assert.True(difference.ExpectedColumn!.IsNullable);
        Assert.True(difference.ExpectedColumn.HasStoredDefault);
        Assert.Null(difference.ActualColumn);
    }

    [Fact]
    public void An_extra_column_reports_the_actual_column()
    {
        var snapshot = Snapshot(Table(
            "orders",
            [
                Column("id", "integer", false),
                Column("legacy_flag", "boolean", true),
            ]));
        var expected = Expected(Table("orders", [Column("id", "integer", false)]));

        var difference = Assert.Single(SchemaEvidenceComparer.Compare(snapshot, expected));

        Assert.Equal(SchemaDifferenceKind.ExtraColumn, difference.Kind);
        Assert.Equal("legacy_flag", difference.ColumnName);
        Assert.Same(snapshot.Tables[0].Columns[1], difference.ActualColumn);
        Assert.Null(difference.ExpectedColumn);
    }

    [Fact]
    public void Type_strings_mismatch_exactly_without_dialect_normalization()
    {
        var snapshot = Snapshot(Table("orders", [Column("id", "int4", false)]));
        var expected = Expected(Table("orders", [Column("id", "integer", false)]));

        var difference = Assert.Single(SchemaEvidenceComparer.Compare(snapshot, expected));

        Assert.Equal(SchemaDifferenceKind.ColumnTypeMismatch, difference.Kind);
        Assert.Equal("id", difference.ColumnName);
        Assert.Equal("integer", difference.ExpectedColumn!.DataType);
        Assert.Equal("int4", difference.ActualColumn!.DataType);
    }

    [Fact]
    public void Nullability_and_identity_mismatches_are_separate_differences()
    {
        var snapshot = Snapshot(Table(
            "orders",
            [Column("id", "integer", true, SchemaIdentityKind.ByDefault)]));
        var expected = Expected(Table(
            "orders",
            [Column("id", "integer", false, SchemaIdentityKind.Always)]));

        var differences = SchemaEvidenceComparer.Compare(snapshot, expected);

        Assert.Equal(2, differences.Count);
        Assert.Equal(SchemaDifferenceKind.ColumnNullabilityMismatch, differences[0].Kind);
        Assert.False(differences[0].ExpectedColumn!.IsNullable);
        Assert.True(differences[0].ActualColumn!.IsNullable);
        Assert.Equal(SchemaDifferenceKind.ColumnIdentityMismatch, differences[1].Kind);
        Assert.Equal(SchemaIdentityKind.Always, differences[1].ExpectedColumn!.IdentityKind);
        Assert.Equal(SchemaIdentityKind.ByDefault, differences[1].ActualColumn!.IdentityKind);
    }

    [Fact]
    public void Primary_key_absence_and_column_changes_are_mismatches()
    {
        var absent = SchemaEvidenceComparer.Compare(
            Snapshot(Table("orders", [Column("id", "integer", false)])),
            Expected(Table(
                "orders",
                [Column("id", "integer", false)],
                new SchemaPrimaryKey(["id"]))));

        var absentDifference = Assert.Single(absent);
        Assert.Equal(SchemaDifferenceKind.PrimaryKeyMismatch, absentDifference.Kind);
        Assert.NotNull(absentDifference.ExpectedPrimaryKey);
        Assert.Null(absentDifference.ActualPrimaryKey);

        var changed = SchemaEvidenceComparer.Compare(
            Snapshot(Table(
                "orders",
                [Column("id", "integer", false), Column("tenant_id", "integer", false)],
                new SchemaPrimaryKey(["id", "tenant_id"]))),
            Expected(Table(
                "orders",
                [Column("id", "integer", false), Column("tenant_id", "integer", false)],
                new SchemaPrimaryKey(["id"]))));

        var changedDifference = Assert.Single(changed);
        Assert.Equal(SchemaDifferenceKind.PrimaryKeyMismatch, changedDifference.Kind);
        Assert.Equal(["id"], changedDifference.ExpectedPrimaryKey!.Columns);
        Assert.Equal(["id", "tenant_id"], changedDifference.ActualPrimaryKey!.Columns);
    }

    [Fact]
    public void Foreign_keys_mismatch_when_missing_extra_or_rule_changed()
    {
        var expectedForeignKey = new SchemaForeignKey(
            ["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.Cascade);
        var actualForeignKey = new SchemaForeignKey(
            ["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.NoAction);

        var missing = SchemaEvidenceComparer.Compare(
            Snapshot(Table("orders", [Column("id", "integer", false), Column("customer_id", "integer", false)])),
            Expected(Table(
                "orders",
                [Column("id", "integer", false), Column("customer_id", "integer", false)],
                foreignKeys: [expectedForeignKey])));

        var missingDifference = Assert.Single(missing);
        Assert.Equal(SchemaDifferenceKind.ForeignKeyMismatch, missingDifference.Kind);
        Assert.Same(expectedForeignKey, missingDifference.ExpectedForeignKey);
        Assert.Null(missingDifference.ActualForeignKey);

        var changed = SchemaEvidenceComparer.Compare(
            Snapshot(Table(
                "orders",
                [Column("id", "integer", false), Column("customer_id", "integer", false)],
                foreignKeys: [actualForeignKey])),
            Expected(Table(
                "orders",
                [Column("id", "integer", false), Column("customer_id", "integer", false)],
                foreignKeys: [expectedForeignKey])));

        var changedDifference = Assert.Single(changed);
        Assert.Equal(SchemaDifferenceKind.ForeignKeyMismatch, changedDifference.Kind);
        Assert.Same(expectedForeignKey, changedDifference.ExpectedForeignKey);
        Assert.Same(actualForeignKey, changedDifference.ActualForeignKey);

        var extra = SchemaEvidenceComparer.Compare(
            Snapshot(Table(
                "orders",
                [Column("id", "integer", false), Column("customer_id", "integer", false)],
                foreignKeys: [actualForeignKey])),
            Expected(Table("orders", [Column("id", "integer", false), Column("customer_id", "integer", false)])));

        var extraDifference = Assert.Single(extra);
        Assert.Equal(SchemaDifferenceKind.ForeignKeyMismatch, extraDifference.Kind);
        Assert.Null(extraDifference.ExpectedForeignKey);
        Assert.Same(actualForeignKey, extraDifference.ActualForeignKey);
    }

    [Fact]
    public void Matching_foreign_keys_with_different_shapes_do_not_cross_match()
    {
        // Two same-shaped foreign keys with different rules exist on both sides: each side's
        // rule pairs with itself, so nothing is reported.
        var snapshot = Snapshot(Table(
            "orders",
            [Column("id", "integer", false), Column("customer_id", "integer", false)],
            foreignKeys:
            [
                new SchemaForeignKey(["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.Cascade),
                new SchemaForeignKey(["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.SetNull),
            ]));
        var expected = Expected(Table(
            "orders",
            [Column("id", "integer", false), Column("customer_id", "integer", false)],
            foreignKeys:
            [
                new SchemaForeignKey(["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.SetNull),
                new SchemaForeignKey(["customer_id"], "customers", ["id"], SchemaForeignKeyDeleteRule.Cascade),
            ]));

        Assert.Empty(SchemaEvidenceComparer.Compare(snapshot, expected));
    }

    [Fact]
    public void Indexes_mismatch_when_missing_extra_or_uniqueness_changed()
    {
        var expectedIndex = new SchemaIndex(["external_ref"], isUnique: true);

        var missing = SchemaEvidenceComparer.Compare(
            Snapshot(Table("orders", [Column("id", "integer", false), Column("external_ref", "text", true)])),
            Expected(Table(
                "orders",
                [Column("id", "integer", false), Column("external_ref", "text", true)],
                indexes: [expectedIndex])));

        var missingDifference = Assert.Single(missing);
        Assert.Equal(SchemaDifferenceKind.IndexMismatch, missingDifference.Kind);
        Assert.Same(expectedIndex, missingDifference.ExpectedIndex);
        Assert.Null(missingDifference.ActualIndex);

        var changed = SchemaEvidenceComparer.Compare(
            Snapshot(Table(
                "orders",
                [Column("id", "integer", false), Column("external_ref", "text", true)],
                indexes: [new SchemaIndex(["external_ref"], isUnique: false)])),
            Expected(Table(
                "orders",
                [Column("id", "integer", false), Column("external_ref", "text", true)],
                indexes: [expectedIndex])));

        var changedDifference = Assert.Single(changed);
        Assert.Equal(SchemaDifferenceKind.IndexMismatch, changedDifference.Kind);
        Assert.True(changedDifference.ExpectedIndex!.IsUnique);
        Assert.False(changedDifference.ActualIndex!.IsUnique);

        var extra = SchemaEvidenceComparer.Compare(
            Snapshot(Table(
                "orders",
                [Column("id", "integer", false), Column("external_ref", "text", true)],
                indexes: [new SchemaIndex(["external_ref"], isUnique: false)])),
            Expected(Table("orders", [Column("id", "integer", false), Column("external_ref", "text", true)])));

        var extraDifference = Assert.Single(extra);
        Assert.Equal(SchemaDifferenceKind.IndexMismatch, extraDifference.Kind);
        Assert.Null(extraDifference.ExpectedIndex);
        Assert.NotNull(extraDifference.ActualIndex);
    }

    [Fact]
    public void Many_differences_at_once_are_all_reported_in_a_deterministic_order()
    {
        // One expected table with one type-mismatched column, one missing column, and a primary
        // key the snapshot lacks; one snapshot table with an extra column, an unexpected index,
        // and a foreign key the expected side does not have; plus one missing table and one
        // extra table.
        var snapshot = Snapshot(
            Table(
                "orders",
                [
                    Column("id", "bigint", false),
                    Column("legacy_flag", "boolean", true),
                ],
                new SchemaPrimaryKey(["id"]),
                foreignKeys:
                [
                    new SchemaForeignKey(["tenant_id"], "tenants", ["id"], SchemaForeignKeyDeleteRule.Cascade),
                ],
                indexes: [new SchemaIndex(["legacy_flag"], isUnique: false)]),
            Table("audit_old", [Column("id", "integer", false)]));
        var expected = Expected(
            Table(
                "orders",
                [
                    Column("id", "integer", false),
                    Column("note", "text", true),
                ],
                new SchemaPrimaryKey(["id"]),
                indexes: [new SchemaIndex(["note"], isUnique: false)]),
            Table("invoices", [Column("id", "integer", false)]));

        var differences = SchemaEvidenceComparer.Compare(snapshot, expected);

        var kinds = differences.Select(difference => difference.Kind).ToArray();
        Assert.Equal(
        [
            // Table "orders": type mismatch and missing column in expected order, then the
            // extra snapshot column, then the primary key (equal, no difference), then the
            // extra snapshot foreign key, then the missing expected index and the extra
            // snapshot index.
            SchemaDifferenceKind.ColumnTypeMismatch,
            SchemaDifferenceKind.MissingColumn,
            SchemaDifferenceKind.ExtraColumn,
            SchemaDifferenceKind.ForeignKeyMismatch,
            SchemaDifferenceKind.IndexMismatch,
            SchemaDifferenceKind.IndexMismatch,
            // Then the missing expected table and the extra snapshot table.
            SchemaDifferenceKind.MissingTable,
            SchemaDifferenceKind.ExtraTable,
        ], kinds);
    }

    [Fact]
    public void Dimensions_outside_the_comparison_contract_produce_no_difference()
    {
        // The stored-default evidence flag is not a comparison dimension: flipping it on either
        // side alone, or on both sides in opposite directions, changes nothing.
        var snapshot = Snapshot(Table(
            "orders",
            [Column("id", "integer", false, hasStoredDefault: true)]));
        var expected = Expected(Table(
            "orders",
            [Column("id", "integer", false, hasStoredDefault: false)]));

        Assert.Empty(SchemaEvidenceComparer.Compare(snapshot, expected));
    }

    [Fact]
    public void Difference_renderings_stay_free_of_sql_and_connection_values()
    {
        const string sqlText =
            "SELECT * FROM pg_class WHERE Host=db.internal Password=top-secret ConnectionString=Host=db.internal";
        var snapshot = Snapshot(Table("orders", [Column("id", "int4", false)]));
        var expected = Expected(Table("orders", [Column("id", "integer", false)]));

        var differences = SchemaEvidenceComparer.Compare(snapshot, expected);
        var rendered = string.Join("; ", differences.Select(difference => difference.ToString()));

        Assert.DoesNotContain("SELECT", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(sqlText, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var snapshot = Snapshot();
        var expected = Expected();

        Assert.Throws<ArgumentNullException>(
            () => SchemaEvidenceComparer.Compare(null!, expected));
        Assert.Throws<ArgumentNullException>(
            () => SchemaEvidenceComparer.Compare(snapshot, null!));
    }

    private static SchemaSnapshot Snapshot(params SchemaTable[] tables) => new(tables);

    private static ExpectedSchema Expected(params SchemaTable[] tables) => new(tables);

    private static SchemaTable Table(
        string name,
        IReadOnlyList<SchemaColumn> columns,
        SchemaPrimaryKey? primaryKey = null,
        IReadOnlyList<SchemaForeignKey>? foreignKeys = null,
        IReadOnlyList<SchemaIndex>? indexes = null,
        string? schema = null) =>
        new(name, columns, primaryKey, foreignKeys, indexes, schema);

    private static SchemaColumn Column(
        string name,
        string dataType,
        bool isNullable,
        SchemaIdentityKind identityKind = SchemaIdentityKind.None,
        bool hasStoredDefault = false) =>
        new(name, dataType, isNullable, identityKind, hasStoredDefault);
}

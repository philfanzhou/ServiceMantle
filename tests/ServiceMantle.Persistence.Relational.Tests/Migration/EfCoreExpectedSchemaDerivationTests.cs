using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational.Migration;
using Xunit;

namespace ServiceMantle.Persistence.Relational.Tests.Migration;

/// <summary>
/// Unit tests for <see cref="EfCoreExpectedSchemaDerivation"/>: every snapshot dimension derived
/// from a real PostgreSQL-mapped EF model (tables with schema, columns with identity kind and the
/// stored-default evidence bit, primary keys, foreign keys, non-constraint indexes), the per-branch
/// value-generation and delete-behavior mappings, determinism, and argument validation. The models
/// are built in memory only; nothing connects to a database.
/// </summary>
public sealed class EfCoreExpectedSchemaDerivationTests
{
    [Fact]
    public void Derive_rejects_a_null_model()
    {
        Assert.Throws<ArgumentNullException>(() => EfCoreExpectedSchemaDerivation.Derive(null!));
    }

    [Fact]
    public void Tables_carry_name_and_schema_and_views_are_not_derived()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents", "app");
                entity.HasKey(x => x.Id);
            })
            .Entity<Plain>(entity =>
            {
                entity.ToTable("plain_table");
                entity.HasKey(x => x.Id);
            })
            .Entity<ViewBound>(entity => entity.ToView("bound_view").HasNoKey()));

        Assert.Equal(2, schema.Tables.Count);
        var app = schema.Tables.Single(table => table.Name == "parents");
        var plain = schema.Tables.Single(table => table.Name == "plain_table");
        Assert.Equal("app", app.Schema);
        Assert.Null(plain.Schema);
    }

    [Fact]
    public void Columns_carry_name_store_type_and_nullability()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents", "app");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired(false);
            }));

        // Column shapes across the whole entity: the key column is not nullable, the optional
        // one is, and both carry the provider store type the reader is expected to align with.
        var columns = schema.Tables.Single().Columns;
        Assert.Equal(4, columns.Count);
        var id = Assert.Single(columns, column => column.Name == "Id");
        var name = Assert.Single(columns, column => column.Name == "Name");
        Assert.Equal("bigint", id.DataType);
        Assert.False(id.IsNullable);
        Assert.Equal("text", name.DataType);
        Assert.True(name.IsNullable);
    }

    [Fact]
    public void Stored_default_evidence_is_true_only_for_configured_defaults()
    {
        var schema = Derive(model => model
            .Entity<DefaultsEntity>(entity =>
            {
                entity.ToTable("defaults");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.WithValueDefault).HasDefaultValue(7);
                entity.Property(x => x.WithSqlDefault).HasDefaultValueSql("now()");
            }));

        var columns = schema.Tables.Single().Columns;
        Assert.True(columns.Single(c => c.Name == "WithValueDefault").HasStoredDefault);
        Assert.True(columns.Single(c => c.Name == "WithSqlDefault").HasStoredDefault);
        Assert.False(columns.Single(c => c.Name == "NoDefault").HasStoredDefault);
        Assert.False(columns.Single(c => c.Name == "Id").HasStoredDefault);
    }

    [Fact]
    public void Primary_key_columns_keep_the_model_order_and_missing_keys_derive_null()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents");
                entity.HasKey(x => new { x.TenantId, x.Code });
            })
            .Entity<Keyless>(entity => entity.ToTable("keyless").HasNoKey()));

        var parents = schema.Tables.Single(table => table.Name == "parents");
        Assert.NotNull(parents.PrimaryKey);
        Assert.Equal(["TenantId", "Code"], parents.PrimaryKey!.Columns);

        var keyless = schema.Tables.Single(table => table.Name == "keyless");
        Assert.Null(keyless.PrimaryKey);
    }

    [Fact]
    public void Foreign_keys_derive_shape_referenced_table_and_rule_without_names()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents", "app");
                entity.HasKey(x => new { x.TenantId, x.Code });
            })
            .Entity<Child>(entity =>
            {
                entity.ToTable("children", "other");
                entity.HasKey(x => x.Id);
                entity.HasOne<Parent>()
                    .WithMany()
                    .HasForeignKey(x => new { x.ParentTenant, x.ParentCode })
                    .HasPrincipalKey(x => new { x.TenantId, x.Code })
                    .OnDelete(DeleteBehavior.Cascade);
            }));

        var child = schema.Tables.Single(table => table.Name == "children");
        var foreignKey = Assert.Single(child.ForeignKeys);
        // The neutral model carries no constraint names: the derived key is fully described by its
        // shape (columns, referenced schema/table/columns) and the delete rule alone.
        Assert.Equal(["ParentTenant", "ParentCode"], foreignKey.Columns);
        Assert.Equal("parents", foreignKey.ReferencedTable);
        Assert.Equal("app", foreignKey.ReferencedSchema);
        Assert.Equal(["TenantId", "Code"], foreignKey.ReferencedColumns);
        Assert.Equal(SchemaForeignKeyDeleteRule.Cascade, foreignKey.DeleteRule);
    }

    [Theory]
    [InlineData(DeleteBehavior.Cascade, SchemaForeignKeyDeleteRule.Cascade)]
    [InlineData(DeleteBehavior.Restrict, SchemaForeignKeyDeleteRule.Restrict)]
    [InlineData(DeleteBehavior.SetNull, SchemaForeignKeyDeleteRule.SetNull)]
    [InlineData(DeleteBehavior.NoAction, SchemaForeignKeyDeleteRule.NoAction)]
    // Client* behaviors take no server-side action, so the relational-side rule they derive is
    // NoAction — the mapping follows the relationship's actual delete rule, not the client view.
    [InlineData(DeleteBehavior.ClientCascade, SchemaForeignKeyDeleteRule.NoAction)]
    [InlineData(DeleteBehavior.ClientSetNull, SchemaForeignKeyDeleteRule.NoAction)]
    [InlineData(DeleteBehavior.ClientNoAction, SchemaForeignKeyDeleteRule.NoAction)]
    public void Delete_behaviors_map_to_relational_delete_rules_per_branch(
        DeleteBehavior behavior,
        SchemaForeignKeyDeleteRule expectedRule)
    {
        var schema = Derive(model => model
            .Entity<Principal>(entity =>
            {
                entity.ToTable("principals", "app");
                entity.HasKey(x => x.Id);
            })
            .Entity<Dependent>(entity =>
            {
                entity.ToTable("dependents");
                entity.HasKey(x => x.Id);
                entity.HasOne<Principal>()
                    .WithMany()
                    .HasForeignKey(x => x.PrincipalId)
                    .OnDelete(behavior);
            }));

        var foreignKey = Assert.Single(
            schema.Tables.Single(table => table.Name == "dependents").ForeignKeys);
        Assert.Equal(expectedRule, foreignKey.DeleteRule);
    }

    [Theory]
    [InlineData(IdentityConfiguration.Always, SchemaIdentityKind.Always)]
    [InlineData(IdentityConfiguration.ByDefault, SchemaIdentityKind.ByDefault)]
    [InlineData(IdentityConfiguration.Serial, SchemaIdentityKind.None)]
    [InlineData(IdentityConfiguration.None, SchemaIdentityKind.None)]
    public void Value_generation_strategies_map_to_identity_kinds_per_branch(
        IdentityConfiguration configuration,
        SchemaIdentityKind expectedKind)
    {
        var schema = Derive(CreateIdentityModel(configuration));

        var column = schema.Tables.Single().Columns.Single(c => c.Name == "Value");
        Assert.Equal(expectedKind, column.IdentityKind);
    }

    [Fact]
    public void ValueGeneratedNever_explicitly_derives_none()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).ValueGeneratedNever();
            }));

        Assert.Equal(
            SchemaIdentityKind.None,
            schema.Tables.Single().Columns.Single(c => c.Name == "Name").IdentityKind);
    }

    [Fact]
    public void Indexes_derive_columns_and_uniqueness_without_names()
    {
        var schema = Derive(model => model
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents", "app");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
                entity.HasIndex(x => x.Name);
            }));

        var indexes = schema.Tables.Single().Indexes;
        Assert.Equal(2, indexes.Count);
        var unique = indexes.Single(index => index.Columns.Count == 2);
        Assert.Equal(["TenantId", "Code"], unique.Columns);
        Assert.True(unique.IsUnique);
        var plain = indexes.Single(index => index.Columns.Count == 1);
        Assert.Equal(["Name"], plain.Columns);
        Assert.False(plain.IsUnique);
    }

    [Fact]
    public void Deriving_the_same_model_twice_produces_identical_schemas()
    {
        using var context = CreateContext(CreateIdentityModel(IdentityConfiguration.Mixed));

        var first = EfCoreExpectedSchemaDerivation.Derive(context.Model);
        var second = EfCoreExpectedSchemaDerivation.Derive(context.Model);

        Assert.Equal(
            first.Tables.Select(table => table.ToString()),
            second.Tables.Select(table => table.ToString()));
        Assert.Equal(
            first.Tables.SelectMany(table => table.Columns.Select(column => column.ToString())),
            second.Tables.SelectMany(table => table.Columns.Select(column => column.ToString())));
    }

    [Fact]
    public void Identically_configured_models_derive_identical_schemas()
    {
        using var firstContext = CreateContext(CreateIdentityModel(IdentityConfiguration.Mixed));
        using var secondContext = CreateContext(CreateIdentityModel(IdentityConfiguration.Mixed));
        var first = EfCoreExpectedSchemaDerivation.Derive(firstContext.Model);
        var second = EfCoreExpectedSchemaDerivation.Derive(secondContext.Model);

        Assert.Equal(
            first.Tables.SelectMany(table => table.Columns.Select(column => column.ToString())),
            second.Tables.SelectMany(table => table.Columns.Select(column => column.ToString())));
    }

    [Fact]
    public void Tables_are_ordered_by_schema_then_name()
    {
        var schema = Derive(model => model
            .Entity<Plain>(entity =>
            {
                entity.ToTable("zebra", "beta");
                entity.HasKey(x => x.Id);
            })
            .Entity<Parent>(entity =>
            {
                entity.ToTable("parents", "beta");
                entity.HasKey(x => x.Id);
            })
            .Entity<Keyless>(entity => entity.ToTable("alpha").HasNoKey()));

        Assert.Equal(
            ["alpha", "beta.parents", "beta.zebra"],
            schema.Tables.Select(table => table.ToString()));
    }

    [Fact]
    public void Extended_evidence_uses_real_names_copies_includes_and_compares_strictly()
    {
        using var context = CreateContext(model => model
            .Entity<Parent>(e =>
            {
                e.ToTable("parents", "app");
                e.HasKey(x => x.Id).HasName("pk_custom");
                e.HasIndex(x => x.Name, "first").HasDatabaseName("ix_first");
                e.HasIndex(x => x.Name, "second").HasDatabaseName("ix_second");
                e.Property(x => x.Code).HasColumnName("code_store");
            })
            .Entity<Dependent>(e =>
            {
                e.ToTable("dependents", "app");
                e.HasKey(x => x.Id);
                e.HasOne<Parent>().WithMany().HasForeignKey(x => x.PrincipalId).HasConstraintName("fk_custom");
            }));
        var included = new List<string> { "code_store" };
        var options = new EfCoreExpectedSchemaDerivationOptions(true, index => index.Name == "ix_first" ? included : null);
        var expected = EfCoreExpectedSchemaDerivation.Derive(context.Model, options);
        var parent = expected.Tables.Single(t => t.Name == "parents");
        Assert.Equal("pk_custom", parent.PrimaryKey!.Name);
        Assert.Equal("fk_custom", expected.Tables.Single(t => t.Name == "dependents").ForeignKeys[0].Name);
        Assert.Equal(["ix_first", "ix_second"], parent.Indexes.Select(i => i.Name));
        Assert.Equal(["code_store"], parent.Indexes[0].IncludedColumns);
        Assert.Empty(parent.Indexes[1].IncludedColumns);
        Assert.All(parent.Indexes, i => Assert.Equal(i.Columns.Count, i.KeyColumnCount));
        var second = EfCoreExpectedSchemaDerivation.Derive(context.Model, options);
        Assert.Empty(SchemaEvidenceComparer.Compare(new SchemaSnapshot(expected.Tables), second,
            new SchemaEvidenceComparisonOptions(true, true)));
        included.Clear();
        Assert.Equal(["code_store"], parent.Indexes[0].IncludedColumns);
        var changed = new SchemaTable(parent.Name, parent.Columns, new SchemaPrimaryKey(parent.PrimaryKey.Columns, "wrong"),
            parent.ForeignKeys, parent.Indexes.Select(i => new SchemaIndex(i.Columns, i.IsUnique, i.Name, 2, [])).ToList(), parent.Schema);
        var differences = SchemaEvidenceComparer.Compare(new SchemaSnapshot([changed]), new ExpectedSchema([parent]),
            new SchemaEvidenceComparisonOptions(true, true));
        Assert.Contains(differences, d => d.Kind == SchemaDifferenceKind.NameMismatch);
        Assert.Contains(differences, d => d.Kind == SchemaDifferenceKind.IndexKeyColumnCountMismatch);
        Assert.Contains(differences, d => d.Kind == SchemaDifferenceKind.IndexIncludedColumnsMismatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nsecret")]
    public void Invalid_include_output_and_resolver_exceptions_fail_without_echo_or_inner(string value)
    {
        using var context = CreateContext(m => m.Entity<Parent>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Name); }));
        var error = Assert.Throws<InvalidOperationException>(() => EfCoreExpectedSchemaDerivation.Derive(context.Model,
            new EfCoreExpectedSchemaDerivationOptions(true, _ => [value])));
        Assert.Equal("Extended schema evidence could not be derived.", error.Message);
        Assert.Null(error.InnerException);
        var thrown = Assert.Throws<InvalidOperationException>(() => EfCoreExpectedSchemaDerivation.Derive(context.Model,
            new EfCoreExpectedSchemaDerivationOptions(true, _ => throw new Exception("secret"))));
        Assert.DoesNotContain("secret", thrown.Message);
        Assert.Null(thrown.InnerException);
        var legacy = EfCoreExpectedSchemaDerivation.Derive(context.Model,
            new EfCoreExpectedSchemaDerivationOptions(false, _ => throw new Exception("must not run")));
        Assert.Null(legacy.Tables[0].PrimaryKey!.Name);
        Assert.Null(legacy.Tables[0].Indexes[0].Name);
    }

    private static Action<ModelBuilder> CreateIdentityModel(IdentityConfiguration configuration) =>
        model => model.Entity<IdentityEntity>(entity =>
        {
            entity.ToTable("identity_table");
            entity.HasKey(x => x.Id);
            switch (configuration)
            {
                case IdentityConfiguration.Always:
                    entity.Property(x => x.Value).UseIdentityAlwaysColumn();
                    break;
                case IdentityConfiguration.ByDefault:
                    entity.Property(x => x.Value).UseIdentityByDefaultColumn();
                    break;
                case IdentityConfiguration.Serial:
                    entity.Property(x => x.Value).UseSerialColumn();
                    break;
                case IdentityConfiguration.Mixed:
                    entity.Property(x => x.Value).UseIdentityAlwaysColumn();
                    entity.Property(x => x.SecondValue).UseIdentityByDefaultColumn();
                    entity.Property(x => x.SerialValue).UseSerialColumn();
                    entity.Property(x => x.PlainValue);
                    break;
            }
        });

    private static ExpectedSchema Derive(Action<ModelBuilder> configure)
    {
        using var context = CreateContext(configure);
        return EfCoreExpectedSchemaDerivation.Derive(context.Model);
    }

    private static DeriveContext CreateContext(Action<ModelBuilder> configure) =>
        new(
            new DbContextOptionsBuilder<DeriveContext>()
                .UseNpgsql("Host=localhost;Database=schema_derivation_unit_tests")
                // EF's default model cache key is the context type alone, so every test's
                // differently-configured model would share the first finalized one. The token
                // key factory gives each context its own cache entry instead.
                .ReplaceService<IModelCacheKeyFactory, TokenModelCacheKeyFactory>()
                .Options,
            configure);

    public enum IdentityConfiguration
    {
        Always,
        ByDefault,
        Serial,
        None,
        Mixed
    }

    private sealed class DeriveContext(
        DbContextOptions<DeriveContext> options,
        Action<ModelBuilder> configure) : DbContext(options)
    {
        internal object ModelToken { get; } = new();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => configure(modelBuilder);
    }

    /// <summary>
    /// Keys the model cache on the per-context token so each test model finalizes independently
    /// of the shared context type.
    /// </summary>
    private sealed class TokenModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((DeriveContext)context).ModelToken, designTime);
    }

    private sealed class Parent
    {
        public long Id { get; set; }
        public string TenantId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class Plain
    {
        public long Id { get; set; }
    }

    private sealed class ViewBound
    {
        public long Id { get; set; }
    }

    private sealed class Keyless
    {
        public string Payload { get; set; } = "";
    }

    private sealed class Child
    {
        public long Id { get; set; }
        public string ParentTenant { get; set; } = "";
        public string ParentCode { get; set; } = "";
    }

    private sealed class Principal
    {
        public long Id { get; set; }
    }

    private sealed class Dependent
    {
        public long Id { get; set; }
        public long? PrincipalId { get; set; }
    }

    private sealed class DefaultsEntity
    {
        public long Id { get; set; }
        public int WithValueDefault { get; set; }
        public DateTime WithSqlDefault { get; set; }
        public int NoDefault { get; set; }
    }

    private sealed class IdentityEntity
    {
        public long Id { get; set; }
        public long Value { get; set; }
        public long SecondValue { get; set; }
        public long SerialValue { get; set; }
        public string PlainValue { get; set; } = "";
    }
}

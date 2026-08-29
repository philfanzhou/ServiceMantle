using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class DatabaseProviderIdResolverTests
{
    [Fact]
    public void Canonical_id_resolves_to_the_descriptor_id()
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres"));

        Assert.True(resolver.TryResolveRegisteredId("PostgreSQL", out var canonicalId));
        Assert.Equal("PostgreSQL", canonicalId);
        Assert.Equal("PostgreSQL", resolver.Canonicalize("PostgreSQL"));
    }

    [Fact]
    public void Alias_resolves_to_the_descriptor_id()
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres", "pgsql"));

        Assert.True(resolver.TryResolveRegisteredId("postgres", out var canonicalId));
        Assert.Equal("PostgreSQL", canonicalId);
        Assert.Equal("PostgreSQL", resolver.Canonicalize("pgsql"));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("PoStGrEs")]
    [InlineData("  postgres  ")]
    public void Case_and_whitespace_variants_resolve_to_the_descriptor_casing(string providerId)
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres"));

        Assert.True(resolver.TryResolveRegisteredId(providerId, out var canonicalId));
        Assert.Equal("PostgreSQL", canonicalId);
        Assert.Equal("PostgreSQL", resolver.Canonicalize(providerId));
    }

    [Fact]
    public void Unknown_but_valid_id_is_preserved_as_declared()
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres"));

        // The caller declared this string as its own canonical id. The resolver must not guess
        // that an unregistered value is an alias of something else.
        Assert.False(resolver.TryResolveRegisteredId("Vendor.Custom-Db_1", out var registeredId));
        Assert.Null(registeredId);
        Assert.False(resolver.IsRegistered("Vendor.Custom-Db_1"));

        Assert.True(resolver.TryCanonicalize("  Vendor.Custom-Db_1  ", out var canonicalId));
        Assert.Equal("Vendor.Custom-Db_1", canonicalId);
        Assert.Equal("Vendor.Custom-Db_1", resolver.Canonicalize("Vendor.Custom-Db_1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".leading-dot")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("pg;drop")]
    public void Invalid_id_is_rejected(string providerId)
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres"));

        Assert.False(resolver.TryCanonicalize(providerId, out var canonicalId));
        Assert.Equal(string.Empty, canonicalId);
        Assert.False(resolver.TryResolveRegisteredId(providerId, out _));
        Assert.Throws<ArgumentException>(() => resolver.Canonicalize(providerId));
    }

    [Fact]
    public void Null_id_is_rejected()
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgres"));

        Assert.False(resolver.TryCanonicalize(null, out _));
        Assert.False(resolver.TryResolveRegisteredId(null, out _));
        Assert.Throws<ArgumentNullException>(() => resolver.Canonicalize(null!));
    }

    [Fact]
    public void Id_longer_than_the_syntax_limit_is_rejected()
    {
        var resolver = DatabaseProviderIdResolver.Empty;

        Assert.False(resolver.TryCanonicalize(new string('a', 65), out _));
        Assert.True(resolver.TryCanonicalize(new string('a', 64), out _));
    }

    [Fact]
    public void Duplicate_canonical_ids_are_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateResolver(
            Descriptor("PostgreSQL"),
            Descriptor("postgresql")));

        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void Alias_conflicting_with_another_alias_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CreateResolver(
            Descriptor("ProviderA", "shared"),
            Descriptor("ProviderB", "Shared")));
    }

    [Fact]
    public void Alias_conflicting_with_a_canonical_id_is_rejected_in_either_order()
    {
        Assert.Throws<ArgumentException>(() => CreateResolver(
            Descriptor("SQLite"),
            Descriptor("MySQL", "sqlite")));

        Assert.Throws<ArgumentException>(() => CreateResolver(
            Descriptor("MySQL", "sqlite"),
            Descriptor("SQLite")));
    }

    [Fact]
    public void Alias_equal_to_its_own_canonical_id_is_accepted()
    {
        var resolver = CreateResolver(Descriptor("PostgreSQL", "postgresql", "postgres"));

        Assert.Equal("PostgreSQL", resolver.Canonicalize("POSTGRESQL"));
        Assert.Equal("PostgreSQL", resolver.Canonicalize("postgres"));
    }

    [Fact]
    public void Null_descriptor_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseProviderIdResolver([Descriptor("PostgreSQL"), null!]));

        Assert.Throws<ArgumentNullException>(() => new DatabaseProviderIdResolver(null!));
    }

    [Fact]
    public void Snapshot_is_fixed_at_construction()
    {
        var descriptors = new List<BootstrapDatabaseProviderDescriptor>
        {
            Descriptor("PostgreSQL", "postgres")
        };
        var resolver = new DatabaseProviderIdResolver(descriptors);

        descriptors.Add(Descriptor("SQLite", "sqlite3"));

        Assert.Equal(2, resolver.Count);
        Assert.False(resolver.IsRegistered("sqlite3"));
        Assert.Equal("sqlite3", resolver.Canonicalize("sqlite3"));
    }

    [Fact]
    public void Empty_snapshot_resolves_every_valid_id_to_itself()
    {
        var resolver = DatabaseProviderIdResolver.Empty;

        Assert.Equal(0, resolver.Count);
        Assert.False(resolver.IsRegistered("PostgreSQL"));
        Assert.Equal("PostgreSQL", resolver.Canonicalize(" PostgreSQL "));
    }

    [Fact]
    public void Registry_exposes_the_same_snapshot_for_every_caller()
    {
        var registry = new BootstrapDatabaseProviderRegistry(
            [new FakeBootstrapProvider(Descriptor("PostgreSQL", "postgres"))]);

        Assert.Same(registry.ProviderIdResolver, registry.ProviderIdResolver);
        Assert.Equal("PostgreSQL", registry.ProviderIdResolver.Canonicalize("postgres"));
    }

    internal static BootstrapDatabaseProviderDescriptor Descriptor(
        string id,
        params string[] aliases) =>
        new(
            id,
            id,
            BootstrapDatabaseTargetKind.ServerDatabase,
            BootstrapServerVersionRequirement.Optional,
            aliases);

    private static DatabaseProviderIdResolver CreateResolver(
        params BootstrapDatabaseProviderDescriptor[] descriptors) =>
        new(descriptors);
}

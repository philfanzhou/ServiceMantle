using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class BootstrapDatabaseConfigurationTests
{
    [Fact]
    public void MySQL_and_legacy_provider_ids_are_supported()
    {
        var config = new BootstrapDatabaseConfiguration(" MySQL ", "15", "Host=db;Password=legacy");
        Assert.Equal("MySQL", config.Provider);

        var sqlite = new BootstrapDatabaseConfiguration("SQLite", "3", "Data Source=app.db;Password=secret");
        Assert.Equal("SQLite", sqlite.Provider);
    }

    [Fact]
    public void Provider_id_is_trimmed_and_sanitized()
    {
        var configuration = new BootstrapDatabaseConfiguration("  SignaCore-Prod  ", "15", "Host=db;Password=safe");

        Assert.Equal("SignaCore-Prod", configuration.Provider);
    }

    [Fact]
    public void Empty_provider_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseConfiguration("   ", "15", "Host=db"));
    }

    [Theory]
    [InlineData("Not Allowed")]
    [InlineData("a/b")]
    [InlineData("bad\\name")]
    [InlineData("bad\tname")]
    public void Invalid_characters_in_provider_are_rejected(string provider)
    {
        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseConfiguration(provider, "15", "Host=db"));
    }

    [Fact]
    public void Too_long_provider_id_is_rejected()
    {
        var provider = new string('a', 65);

        Assert.Throws<ArgumentException>(() => new BootstrapDatabaseConfiguration(provider, "15", "Host=db"));
    }

    [Fact]
    public void Legal_third_party_provider_is_accepted()
    {
        var configuration = new BootstrapDatabaseConfiguration("Acme.Enterprise_Provider-1", "15", "Host=db;Password=secret");

        Assert.Equal("Acme.Enterprise_Provider-1", configuration.Provider);
    }
}


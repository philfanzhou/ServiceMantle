using System.Text.Json;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

public sealed class BootstrapFileStoreTests
{
    [Fact]
    public void Load_reads_a_complete_version_one_file()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);
        File.WriteAllText(store.FilePath, """
            {
              "FormatVersion": 1,
              "ServiceId": "signacore",
              "Database": {
                "Provider": "PostgreSQL",
                "ServerVersion": "15",
                "ConnectionString": "Host=db;Database=signacore;Password=test-password"
              },
              "MasterKey": "test-master-key"
            }
            """);

        var configuration = store.Load();

        Assert.Equal(serviceId, configuration.ServiceId);
        Assert.Equal("PostgreSQL", configuration.Database.Provider);
        Assert.Equal("15", configuration.Database.ServerVersion);
        Assert.Equal("Host=db;Database=signacore;Password=test-password", configuration.Database.ConnectionString);
        Assert.Equal("test-master-key", configuration.MasterKey);
        Assert.Equal(store.FilePath, configuration.SourcePath);
    }

    [Fact]
    public void Load_reads_the_legacy_signacore_file_using_the_expected_service_id()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": " PostgreSQL ",
                "ServerVersion": "15",
                "ConnectionString": "Host=db;Database=signacore;Password=legacy-password"
              },
              "MasterKey": "legacy-master-key"
            }
            """);

        var configuration = store.Load();

        Assert.Equal(serviceId, configuration.ServiceId);
        Assert.Equal("PostgreSQL", configuration.Database.Provider);
    }

    [Fact]
    public void Load_reads_the_legacy_sqlite_file_using_the_expected_service_id()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": " SQLite ",
                "ConnectionString": "Data Source=legacy.db;Mode=ReadWrite;Password=legacy-password"
              },
              "MasterKey": "legacy-master-key"
            }
            """);

        var configuration = store.Load();

        Assert.Equal(serviceId, configuration.ServiceId);
        Assert.Equal("SQLite", configuration.Database.Provider);
    }

    [Fact]
    public void Create_writes_the_current_format_version_and_service_id()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("SignaCore");
        var store = CreateStore(directory, serviceId);

        store.Create(CreateConfiguration(serviceId));
        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));

        Assert.Equal(1, document.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal("signacore", document.RootElement.GetProperty("ServiceId").GetString());
    }

    [Fact]
    public void Default_path_uses_the_normalized_service_id()
    {
        var serviceId = ServiceId.Parse("  SignaCore-Prod  ");
        var store = new BootstrapFileStore(serviceId, new BootstrapDatabaseProviderRegistry([]));

        Assert.True(Path.IsPathFullyQualified(store.FilePath));
        Assert.Equal("signacore-prod.bootstrap.json", Path.GetFileName(store.FilePath));
    }

    [Fact]
    public void Explicit_path_is_resolved_to_an_absolute_path()
    {
        using var directory = TemporaryDirectory.Create();
        var relativePath = Path.Combine(directory.Path, "custom", "bootstrap.json");
        var store = new BootstrapFileStore(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseProviderRegistry([]),
            relativePath);

        Assert.Equal(Path.GetFullPath(relativePath), store.FilePath);
    }

    [Fact]
    public void TryLoad_returns_null_when_the_file_does_not_exist()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);

        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Load_fails_when_the_file_does_not_exist()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);

        var exception = Assert.Throws<BootstrapException>(() => store.Load());

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_file_fails_without_being_treated_as_missing()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, string.Empty);

        Assert.Throws<BootstrapException>(() => store.TryLoad());
    }

    [Fact]
    public void Corrupt_json_fails_with_safe_location_information()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, "{ \"Database\": ");

        var exception = Assert.Throws<BootstrapException>(() => store.Load());

        Assert.Contains("line", exception.Message, StringComparison.Ordinal);
        Assert.Contains("byte position", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_json_fields_fail_strictly()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": "PostgreSQL",
                "ConnectionString": "Host=db;Password=unknown-field-password"
              },
              "MasterKey": "unknown-field-master-key",
              "Unexpected": true
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Missing_database_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "MasterKey": "missing-database-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Missing_connection_string_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "Database": { "Provider": "PostgreSQL" },
              "MasterKey": "missing-connection-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Missing_master_key_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": "PostgreSQL",
                "ConnectionString": "Host=db;Password=missing-master-password"
              }
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Legal_unregistered_provider_loads_from_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": "AcmeDb",
                "ConnectionString": "Host=db;Password=provider-password"
              },
              "MasterKey": "provider-master-key"
            }
            """);

        var configuration = store.Load();

        Assert.Equal("AcmeDb", configuration.Database.Provider);
        Assert.Equal("provider-master-key", configuration.MasterKey);
    }

    [Theory]
    [InlineData("MySQL")]
    [InlineData("MariaDB")]
    [InlineData("Oracle")]
    [InlineData("SqlServer")]
    [InlineData("MyCompanyDB")]
    public void Create_accepts_well_known_and_third_party_providers(string provider)
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration(provider: provider));

        var loaded = store.Load();
        Assert.Equal(provider, loaded.Database.Provider);
    }

    [Fact]
    public void Whitespace_only_provider_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": "   ",
                "ConnectionString": "Host=db;Password=whitespace-provider-password"
              },
              "MasterKey": "whitespace-provider-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Mismatched_service_id_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, ServiceId.Parse("signacore"));
        File.WriteAllText(store.FilePath, """
            {
              "ServiceId": "other-service",
              "Database": {
                "Provider": "PostgreSQL",
                "ConnectionString": "Host=db;Password=service-id-password"
              },
              "MasterKey": "service-id-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Unsupported_format_version_fails()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, """
            {
              "FormatVersion": 2,
              "Database": {
                "Provider": "PostgreSQL",
                "ConnectionString": "Host=db;Password=version-password"
              },
              "MasterKey": "version-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Create_does_not_overwrite_an_existing_file()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);
        var original = CreateConfiguration(serviceId);
        store.Create(original);
        var originalContents = File.ReadAllText(store.FilePath);

        Assert.Throws<BootstrapException>(() => store.Create(CreateConfiguration(serviceId, "Host=other")));

        Assert.Equal(originalContents, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Replace_atomically_replaces_an_existing_file()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);
        store.Create(CreateConfiguration(serviceId));

        store.Replace(CreateConfiguration(serviceId, "Host=other;Database=signacore"));

        Assert.Equal("Host=other;Database=signacore", store.Load().Database.ConnectionString);
    }

    [Fact]
    public void Replace_fails_when_the_file_does_not_exist()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(directory, serviceId);

        Assert.Throws<BootstrapException>(() => store.Replace(CreateConfiguration(serviceId)));
    }

    [Fact]
    public void Configuration_to_string_does_not_contain_secrets()
    {
        var configuration = CreateConfiguration();

        var text = configuration.ToString();

        Assert.DoesNotContain(configuration.Database.ConnectionString, text, StringComparison.Ordinal);
        Assert.DoesNotContain(configuration.MasterKey, text, StringComparison.Ordinal);
        Assert.Contains("signacore", text, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_exception_does_not_echo_secrets()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        const string connectionSecret = "exception-connection-password";
        const string masterSecret = "exception-master-key";
        File.WriteAllText(store.FilePath, $"{{\"Database\":{{\"Provider\":\"PostgreSQL\",\"ConnectionString\":\"Host=db;Password={connectionSecret}\"}},\"MasterKey\":\"{masterSecret}\",\"Unexpected\":true}}");

        var exception = Assert.Throws<BootstrapException>(() => store.Load());
        var text = exception.ToString();

        Assert.DoesNotContain(connectionSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(masterSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unix_files_are_written_with_private_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);
        store.Create(CreateConfiguration());

        var mode = File.GetUnixFileMode(store.FilePath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Unix_new_configuration_directories_are_private()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var configurationDirectory = Path.Combine(directory.Path, "new-config");
        var store = new BootstrapFileStore(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseProviderRegistry([]),
            Path.Combine(configurationDirectory, "signacore.bootstrap.json"));

        store.Create(CreateConfiguration());

        var mode = File.GetUnixFileMode(configurationDirectory);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode);
    }

    [Fact]
    public void Successful_writes_leave_no_temporary_files()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory);

        store.Create(CreateConfiguration());
        store.Replace(CreateConfiguration(ServiceId.Parse("signacore"), "Host=replaced"));

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private static BootstrapFileStore CreateStore(
        TemporaryDirectory directory,
        ServiceId? serviceId = null,
        BootstrapDatabaseProviderRegistry? providerRegistry = null)
    {
        var actualServiceId = serviceId ?? ServiceId.Parse("signacore");
        var filePath = Path.Combine(directory.Path, "config", "signacore.bootstrap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return new BootstrapFileStore(
            actualServiceId,
            providerRegistry ?? new BootstrapDatabaseProviderRegistry([]),
            filePath);
    }

    private static BootstrapConfiguration CreateConfiguration(
        ServiceId? serviceId = null,
        string connectionString = "Host=db;Database=signacore;Password=test-password",
        string provider = "PostgreSQL")
    {
        var actualServiceId = serviceId ?? ServiceId.Parse("signacore");
        return new BootstrapConfiguration(
            actualServiceId,
            new BootstrapDatabaseConfiguration(provider, "15", connectionString),
            "test-master-key");
    }
}

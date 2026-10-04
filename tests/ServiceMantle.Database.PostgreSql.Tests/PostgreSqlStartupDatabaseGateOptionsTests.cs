using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

public sealed class PostgreSqlStartupDatabaseGateOptionsTests
{
    [Theory]
    [InlineData("Doctheca", false)]
    [InlineData("Doctheca", true)]
    [InlineData("Quaestura", false)]
    [InlineData("Quaestura", true)]
    [InlineData("Ruoyu.Admin", false)]
    [InlineData("Ruoyu.Admin", true)]
    public void Preset_matches_each_consumers_existing_manual_composition(string consumer, bool allow)
    {
        var target = new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, null,
            $"Host=example;Port=5432;Database={consumer};Username=app;Password=preset-sensitive-value");
        var manual = new StartupDatabaseGateOptions(target, DatabaseDeploymentMode.MultiInstance,
            TimeSpan.FromSeconds(30), enableTargetPreparation: true, allowTargetCreation: allow,
            maintenanceConnectionString: PostgreSqlMaintenanceConnection.DeriveConnectionString(target.ConnectionString),
            preparationTimeout: TimeSpan.FromSeconds(30));
        var actual = PostgreSqlStartupDatabaseGateOptions.Create(target, allow);
        Assert.Same(target, actual.Database);
        Assert.Equal(manual.DeploymentMode, actual.DeploymentMode);
        Assert.Equal(manual.LockWaitBudget, actual.LockWaitBudget);
        Assert.Equal(manual.EnableTargetPreparation, actual.EnableTargetPreparation);
        Assert.Equal(manual.AllowTargetCreation, actual.AllowTargetCreation);
        Assert.Equal(manual.PreparationTimeout, actual.PreparationTimeout);
        Assert.Equal(manual.MaintenanceConnectionString, actual.MaintenanceConnectionString);
        Assert.Equal("postgres", new NpgsqlConnectionStringBuilder(actual.MaintenanceConnectionString).Database);
        Assert.DoesNotContain("preset-sensitive-value", actual.ToString()!);
        Assert.DoesNotContain("preset-sensitive-value", target.ToString());
        Assert.False(typeof(PostgreSqlStartupDatabaseGateOptions).GetMethod(nameof(PostgreSqlStartupDatabaseGateOptions.Create))!.GetParameters()[1].HasDefaultValue);
    }
    [Theory]
    [InlineData("PostgreSQL", "Password=preset-sensitive-value;Unknown=preset-sensitive-value", "database.connection_string_invalid")]
    [InlineData("PostgreSQL", "Host=example;Port=preset-sensitive-value", "database.connection_string_invalid")]
    [InlineData("Sqlite", "Password=preset-sensitive-value", "database_target_preparation.invalid_target")]
    public void Invalid_inputs_have_fixed_codes_without_parser_inner_or_input_echo(string provider, string connection, string code)
    {
        var target = new BootstrapDatabaseConfiguration(provider, null, connection);
        var exception = Assert.Throws<ArgumentException>(() => PostgreSqlStartupDatabaseGateOptions.Create(target, false));
        Assert.Contains(code, exception.Message); Assert.Null(exception.InnerException);
        Assert.DoesNotContain("preset-sensitive-value", exception.ToString());
        Assert.DoesNotContain(connection, exception.ToString());
    }
    [Fact]
    public void Null_target_is_rejected_and_case_equivalent_provider_is_accepted()
    {
        Assert.Throws<ArgumentNullException>(() => PostgreSqlStartupDatabaseGateOptions.Create(null!, false));
        var target = new BootstrapDatabaseConfiguration("postgresql", "16", "Host=example;Database=app");
        Assert.Same(target, PostgreSqlStartupDatabaseGateOptions.Create(target, false).Database);
    }
}

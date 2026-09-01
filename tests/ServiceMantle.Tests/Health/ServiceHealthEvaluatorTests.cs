using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Health;

public sealed class ServiceHealthEvaluatorTests
{
    public static TheoryData<
        ServiceStartupPhase,
        ServiceMigrationReadinessState,
        ServiceDatabaseReadinessState> Matrix
    {
        get
        {
            var data = new TheoryData<
                ServiceStartupPhase,
                ServiceMigrationReadinessState,
                ServiceDatabaseReadinessState>();
            foreach (var phase in Enum.GetValues<ServiceStartupPhase>())
            {
                foreach (var migration in Enum.GetValues<ServiceMigrationReadinessState>())
                {
                    foreach (var database in Enum.GetValues<ServiceDatabaseReadinessState>())
                    {
                        data.Add(phase, migration, database);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Finite_matrix_is_always_live_and_has_exactly_one_ready_combination(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var snapshot = new ServiceHealthSnapshot(phase, migration, database);

        var evaluation = ServiceHealthEvaluator.Evaluate(snapshot);

        Assert.True(evaluation.IsLive);
        Assert.Equal(
            phase == ServiceStartupPhase.Completed &&
                migration == ServiceMigrationReadinessState.Succeeded &&
                database == ServiceDatabaseReadinessState.Reachable,
            evaluation.IsReady);
        Assert.Same(snapshot, evaluation.Snapshot);
    }

    [Fact]
    public void Matrix_contains_twenty_four_rows_and_one_ready_result()
    {
        Assert.Equal(24, Matrix.Count);
        var readyCount = 0;
        foreach (var phase in Enum.GetValues<ServiceStartupPhase>())
        {
            foreach (var migration in Enum.GetValues<ServiceMigrationReadinessState>())
            {
                foreach (var database in Enum.GetValues<ServiceDatabaseReadinessState>())
                {
                    if (ServiceHealthEvaluator.Evaluate(
                        new ServiceHealthSnapshot(phase, migration, database)).IsReady)
                    {
                        readyCount++;
                    }
                }
            }
        }

        Assert.Equal(1, readyCount);
    }

    [Fact]
    public void Snapshot_rejects_unknown_states_and_unsafe_error_codes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceHealthSnapshot(
            (ServiceStartupPhase)99,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            (ServiceMigrationReadinessState)99,
            ServiceDatabaseReadinessState.Reachable));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded,
            (ServiceDatabaseReadinessState)99));
        Assert.Throws<ArgumentException>(() => new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Failed,
            ServiceDatabaseReadinessState.Unreachable,
            "Server=db;Password=secret"));
    }

    [Fact]
    public void Snapshot_and_evaluation_are_immutable_safe_projections()
    {
        var snapshot = new ServiceHealthSnapshot(
            ServiceStartupPhase.PendingSetup,
            ServiceMigrationReadinessState.Failed,
            ServiceDatabaseReadinessState.Unreachable,
            "migration.failed");
        var evaluation = ServiceHealthEvaluator.Evaluate(snapshot);

        Assert.All(
            typeof(ServiceHealthSnapshot).GetProperties(),
            property => Assert.Null(property.SetMethod));
        Assert.Contains("migration.failed", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("connection", evaluation.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_health_contract_does_not_reference_AspNetCore()
    {
        Assert.DoesNotContain(
            typeof(ServiceHealthSnapshot).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }
}

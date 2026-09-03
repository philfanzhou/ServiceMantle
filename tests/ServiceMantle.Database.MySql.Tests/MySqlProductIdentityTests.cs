using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

public sealed class MySqlProductIdentityTests
{
    private const string Connection = "Server=private-host;Database=app;User ID=private-user;Password=secret-value";
    private static readonly string[] Paths =
        ["bootstrap", "bootstrap-missing", "bootstrap-denied", "observe", "observe-missing", "observe-denied", "prepare", "prepare-existing"];

    public static IEnumerable<object?[]> ProductMatrix()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs/decisions/mysql-product-samples.json")))
        {
            directory = directory.Parent;
        }

        using var samples = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "docs/decisions/mysql-product-samples.json")));
        foreach (var sample in samples.RootElement.EnumerateArray())
        {
            foreach (var path in Paths)
            {
                yield return [sample.GetProperty("name").GetString(), path,
                    sample.GetProperty("handshake").GetString(), sample.GetProperty("version").GetString(),
                    sample.GetProperty("systemVersion").GetString(), sample.GetProperty("comment").GetString(),
                    sample.GetProperty("accept").GetBoolean()];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProductMatrix))]
    public async Task ADR_samples_control_every_path_before_target_queries_or_creation(
        string name, string path, string handshake, string? version, string? systemVersion, string? comment, bool accept)
    {
        _ = name;
        var session = Session(path, version, systemVersion, comment);
        session.Handshake = handshake;
        session.ThrowOnDispose = true;
        var factory = new SessionFactory(path, session);
        var result = await Run(path, factory.Create, TestContext.Current.CancellationToken);

        AssertResult(path, result, accept ? null : "product");
        Assert.True(session.WasDisposed);
        Assert.Equal(MySqlProductIdentity.Query, session.Commands[0]);
        Assert.Equal(5, session.Timeouts[0]);
        if (!accept || path.EndsWith("-missing", StringComparison.Ordinal) || path.EndsWith("-denied", StringComparison.Ordinal))
        {
            Assert.Equal([MySqlProductIdentity.Query], session.Commands);
        }
        else if (path == "prepare")
        {
            Assert.StartsWith("CREATE DATABASE", session.Commands[^1], StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(session.Commands, command => command.StartsWith("CREATE", StringComparison.Ordinal));
        }

        Assert.Equal(factory.IsFallback ? 2 : 1, factory.Settings.Count);
        if (factory.IsFallback)
        {
            var original = new MySqlConnectionStringBuilder(factory.Settings[0]) { Database = string.Empty };
            Assert.Equal(original.ConnectionString, factory.Settings[1]);
            Assert.True(factory.FailedSession!.WasDisposed);
        }
        if (path.StartsWith("prepare", StringComparison.Ordinal))
        {
            var settings = new MySqlConnectionStringBuilder(factory.Settings[0]);
            Assert.False(settings.Pooling);
            Assert.False(settings.AutoEnlist);
            Assert.Empty(settings.Database);
        }

        AssertSafe(result);
    }

    public static IEnumerable<object[]> FailureMatrix() => Paths.SelectMany(path =>
        new[] { "query-denied", "query-transport", "query-timeout", "query-internal", "open-auth", "open-transport", "open-internal" }
            .Select(failure => new object[] { path, failure }));

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public async Task Failures_have_stable_safe_classification_and_do_not_create(string path, string failure)
    {
        var session = Session(path);
        Exception exception = failure switch
        {
            "query-denied" => SqlFailure(MySqlErrorCode.SpecifiedAccessDeniedError),
            "query-timeout" => SqlFailure(MySqlErrorCode.CommandTimeoutExpired),
            "query-transport" or "open-transport" => new IOException("secret-value;private-host;private-user"),
            "open-auth" => SqlFailure(MySqlErrorCode.AccessDenied),
            _ => new InvalidOperationException("secret-value;private-host;private-user")
        };
        if (failure.StartsWith("open", StringComparison.Ordinal))
        {
            session.Opening = _ => Task.FromException(exception);
        }
        else
        {
            session.Execute = (_, _) => Task.FromException<object?>(exception);
        }
        var factory = new SessionFactory(path, session);
        var result = await Run(path, factory.Create, TestContext.Current.CancellationToken);
        var category = failure switch
        {
            "query-denied" => "product",
            "query-timeout" or "query-transport" or "open-transport" => "transport",
            "open-auth" => "authentication",
            _ => "internal"
        };
        AssertResult(path, result, category);
        AssertSafe(result);
        Assert.DoesNotContain(session.Commands, command => command.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.True(session.WasDisposed);
        Assert.Equal(factory.IsFallback ? 2 : 1, factory.Settings.Count);
    }

    public static IEnumerable<object[]> CancellationMatrix() => Paths.SelectMany(path =>
        new[] { "open", "product" }.Select(stage => new object[] { path, stage }));

    [Theory]
    [MemberData(nameof(CancellationMatrix))]
    public async Task Caller_cancellation_at_each_new_stage_preserves_token_and_discards_raw_exception(string path, string stage)
    {
        using var source = new CancellationTokenSource();
        var session = Session(path);
        Task Cancel()
        {
            source.Cancel();
            throw new InvalidOperationException("secret-value;private-host;private-user");
        }
        if (stage == "open") session.Opening = _ => Cancel();
        else session.Execute = async (_, _) => { await Cancel(); return null; };
        var factory = new SessionFactory(path, session);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => Run(path, factory.Create, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        AssertSafe(exception);
        Assert.True(session.WasDisposed);
        Assert.DoesNotContain(session.Commands, sql => sql.StartsWith("CREATE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("product")]
    public async Task Preparation_deadline_covers_open_and_product_query(string stage)
    {
        var session = Session("prepare");
        if (stage == "open") session.Opening = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        else session.Execute = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; };
        var factory = new SessionFactory("prepare", session);
        var result = await Run("prepare", factory.Create, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, ((DatabaseTargetPreparationResult)result).ErrorCode);
        Assert.True(session.WasDisposed);
        Assert.DoesNotContain(session.Commands, sql => sql.StartsWith("CREATE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("columns")]
    [InlineData("type")]
    [InlineData("null")]
    public async Task Product_row_shape_is_strict(string shape)
    {
        var session = Session("observe");
        session.Execute = (_, _) => Task.FromResult<object?>(shape switch
        {
            "empty" => ScriptedMySqlConnection.Rows(),
            "multiple" => ScriptedMySqlConnection.Rows(["8.4.0", "8.4.0", "MySQL Community Server - GPL"], ["8.4.0", "8.4.0", "MySQL Community Server - GPL"]),
            "columns" => ScriptedMySqlConnection.Rows(["8.4.0", "8.4.0"]),
            "type" => ScriptedMySqlConnection.Rows([8, "8.4.0", "MySQL Community Server - GPL"]),
            _ => ScriptedMySqlConnection.Rows([null, "8.4.0", "MySQL Community Server - GPL"])
        });
        var result = await Run("observe", _ => session, TestContext.Current.CancellationToken);
        AssertResult("observe", result, "product");
        Assert.Single(session.Commands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.4.0\n")]
    [InlineData("8.4.00")]
    [InlineData("8.1000.0")]
    [InlineData("8.٤.0")]
    [InlineData("8.4.0-ndb")]
    [InlineData(" 8.4.0")]
    public void Numeric_signals_reject_noncanonical_forms(string version) =>
        Assert.False(MySqlProductIdentity.IsSupported(version, version, version, "MySQL Community Server - GPL"));

    [Theory]
    [InlineData(MySqlErrorCode.AccessDenied)]
    [InlineData(MySqlErrorCode.UnableToConnectToHost)]
    public async Task Initial_authentication_or_transport_failure_never_falls_back(MySqlErrorCode error)
    {
        var factoryCalls = 0;
        var session = Session("observe");
        session.Opening = _ => Task.FromException(SqlFailure(error));
        await Run("observe", _ => { factoryCalls++; return session; }, TestContext.Current.CancellationToken);
        Assert.Equal(1, factoryCalls);
        Assert.Empty(session.Commands);
    }

    private static async Task<object> Run(string path, Func<MySqlConnectionStringBuilder, DbConnection> factory,
        CancellationToken token, TimeSpan? timeout = null)
    {
        var target = new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.MySql, "8.4", Connection);
        var probe = new MySqlBootstrapProbe(factory);
        if (path.StartsWith("bootstrap", StringComparison.Ordinal))
            return await new MySqlBootstrapDatabaseProvider(probe).ValidateAsync(target, token);
        var provider = new MySqlDatabaseTargetPreparationProvider(probe,
            new MySqlDatabaseCreationProbe(createConnection: factory));
        return path.StartsWith("prepare", StringComparison.Ordinal)
            ? await provider.PrepareAsync(new(target, Connection), timeout ?? TimeSpan.FromSeconds(5), token)
            : await provider.ObserveAsync(target, token);
    }

    private static ScriptedMySqlConnection Session(string path, string? version = "8.4.0",
        string? systemVersion = "8.4.0", string? comment = "MySQL Community Server - GPL") => new()
    {
        Execute = (sql, _) => Task.FromResult<object?>(sql switch
        {
            MySqlProductIdentity.Query => ScriptedMySqlConnection.Rows([version, systemVersion, comment]),
            "SELECT @@lower_case_table_names" => 0,
            _ when sql.Contains("BINARY DATABASE()", StringComparison.Ordinal) => ScriptedMySqlConnection.Rows([0, true, true]),
            _ when sql.Contains("INFORMATION_SCHEMA", StringComparison.Ordinal) => path == "prepare-existing" ? "app" : null,
            _ when sql.StartsWith("CREATE DATABASE", StringComparison.Ordinal) => 1,
            _ => throw new InvalidOperationException("Unexpected SQL.")
        })
    };

    private static void AssertResult(string path, object result, string? failure)
    {
        if (result is BootstrapValidationResult validation)
        {
            Assert.Equal(failure switch
            {
                "product" or "internal" => "database.provider_validation_failed",
                "transport" => "database.connection_failed",
                "authentication" => "database.authentication_failed",
                _ when path.EndsWith("-missing", StringComparison.Ordinal) => "database.target_not_found",
                _ when path.EndsWith("-denied", StringComparison.Ordinal) => "database.permission_denied",
                _ => null
            }, validation.ErrorCode);
        }
        else if (result is DatabaseTargetObservation observation)
        {
            Assert.Equal(failure switch
            {
                "product" => WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
                "internal" => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed,
                "transport" => WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable,
                "authentication" => WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
                _ when path.EndsWith("-denied", StringComparison.Ordinal) => WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                _ => null
            }, observation.ErrorCode);
            if (failure is not null) Assert.Null(observation.TargetExists);
            if (failure == "product") Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
            if (failure is null && path.EndsWith("-missing", StringComparison.Ordinal))
                Assert.Equal(DatabaseTargetObservationStatus.TargetMissing, observation.Status);
        }
        else
        {
            var preparation = (DatabaseTargetPreparationResult)result;
            Assert.Equal(failure switch
            {
                "product" => WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
                "transport" => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
                "authentication" => WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
                "internal" => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed,
                _ => null
            }, preparation.ErrorCode);
            if (failure is null)
                Assert.Equal(path == "prepare-existing" ? DatabaseTargetPreparationOutcome.AlreadyExists : DatabaseTargetPreparationOutcome.Created, preparation.Outcome);
        }
    }

    private static void AssertSafe(object value)
    {
        var text = value.ToString() + (value is Exception ? string.Empty : JsonSerializer.Serialize(value));
        foreach (var secret in new[] { "secret-value", "private-host", "private-user" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    // MySqlConnector exposes no public constructor for a server error. Use its pinned internal
    // constructor only in this script fixture to exercise the production error-number classifier.
    private static MySqlException SqlFailure(MySqlErrorCode code) => (MySqlException)typeof(MySqlException)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(MySqlErrorCode), typeof(string)], null)!
        .Invoke([code, "secret-value;private-host;private-user"]);

    private sealed class SessionFactory(string path, ScriptedMySqlConnection session)
    {
        internal List<string> Settings { get; } = [];
        internal ScriptedMySqlConnection? FailedSession { get; private set; }
        internal bool IsFallback => path.EndsWith("-missing", StringComparison.Ordinal) || path.EndsWith("-denied", StringComparison.Ordinal);
        internal DbConnection Create(MySqlConnectionStringBuilder settings)
        {
            Settings.Add(settings.ConnectionString);
            if (IsFallback && Settings.Count == 1)
            {
                FailedSession = new ScriptedMySqlConnection
                {
                    ThrowOnDispose = true,
                    Opening = _ => Task.FromException(SqlFailure(path.EndsWith("-missing", StringComparison.Ordinal)
                        ? MySqlErrorCode.UnknownDatabase : MySqlErrorCode.DatabaseAccessDenied))
                };
                return FailedSession;
            }
            return session;
        }
    }
}

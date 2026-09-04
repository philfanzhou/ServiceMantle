using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using MySqlConnector;

namespace ServiceMantle.Database.MySql.Tests;

internal sealed class ScriptedMySqlConnection : DbConnection
{
    internal List<string> Commands { get; } = [];
    internal List<int> Timeouts { get; } = [];
    internal Func<string, CancellationToken, Task<object?>> Execute { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected SQL.");
    internal Func<CancellationToken, Task>? Opening { get; set; }
    internal bool ThrowOnDispose { get; set; }
    internal bool WasDisposed { get; private set; }
    internal string Handshake { get; set; } = "8.4.0";
    [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "app";
    public override string DataSource => "private-host";
    public override string ServerVersion => Handshake;
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() { }
    public override void Open() => throw new NotSupportedException();
    public override Task OpenAsync(CancellationToken cancellationToken) =>
        Opening?.Invoke(cancellationToken) ?? Task.CompletedTask;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new ScriptedCommand(this);
    public override ValueTask DisposeAsync()
    {
        WasDisposed = true;
        return ThrowOnDispose
            ? ValueTask.FromException(new InvalidOperationException("Password=secret-value"))
            : ValueTask.CompletedTask;
    }

    internal static DbDataReader Rows(params object?[][] rows)
    {
        var table = new DataTable();
        var width = rows.Length == 0 ? 3 : rows[0].Length;
        for (var index = 0; index < width; index++)
        {
            table.Columns.Add($"c{index}", typeof(object));
        }

        foreach (var row in rows)
        {
            table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
        }

        return table.CreateDataReader();
    }

    private sealed class ScriptedCommand(ScriptedMySqlConnection connection) : DbCommand
    {
        private readonly MySqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new MySqlParameter();
        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        private Task<object?> Run(CancellationToken token)
        {
            connection.Commands.Add(CommandText);
            connection.Timeouts.Add(CommandTimeout);
            return connection.Execute(CommandText, token);
        }
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Run(cancellationToken);
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            (int)(await Run(cancellationToken))!;
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken) =>
            (DbDataReader)(await Run(cancellationToken))!;
    }
}

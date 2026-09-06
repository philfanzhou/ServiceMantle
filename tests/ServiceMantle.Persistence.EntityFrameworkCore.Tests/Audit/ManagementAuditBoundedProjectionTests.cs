using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

public sealed class ManagementAuditBoundedProjectionTests
{
    public static TheoryData<string, int> PersistedTextColumns =>
        new()
        {
            { "id", ManagementAuditEntityMapper.MaxPersistedTextByteLength(36) },
            {
                "operator_id",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditOperator.MaxOperatorIdLength)
            },
            {
                "operator_display_name",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditOperator.MaxDisplayNameLength)
            },
            {
                "operator_source",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditOperatorSource.MaxLength)
            },
            {
                "action",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditAction.MaxLength)
            },
            {
                "target_type",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditTargetType.MaxLength)
            },
            {
                "target_id",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditTarget.MaxTargetIdLength)
            },
            {
                "client_ip",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditEvent.MaxClientIpLength)
            },
            {
                "correlation_id",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditEvent.MaxCorrelationIdLength)
            },
            {
                "security_description",
                ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditEvent.MaxDescriptionLength)
            },
            { "metadata_json", ManagementAuditEntityMapper.MaxMetadataJsonByteLength }
        };

    [Theory]
    [MemberData(nameof(PersistedTextColumns))]
    public async Task QueryAsync_bounds_every_oversized_text_column_before_reader_materialization(
        string column,
        int maximumBytes)
    {
        var observer = new ReaderValueObserver();
        await using var fixture = await SqliteFixture.CreateAsync(observer);
        await fixture.InsertRowAsync(Day(2));
        await fixture.SetOversizedValueAsync(column, maximumBytes, multibyte: false);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            fixture.QueryAsync(pageSize: 1));

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.Contains(observer.CommandTexts, sql =>
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("octet_length", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observer.StringValues, value => value.Length > maximumBytes);
        Assert.DoesNotContain(observer.StringValues, value => value.Contains('\0'));
    }

    [Theory]
    [InlineData("security_description", ManagementAuditEvent.MaxDescriptionLength * 4)]
    [InlineData("metadata_json", ManagementAuditEntityMapper.MaxMetadataJsonByteLength)]
    public async Task QueryAsync_bounds_multibyte_text_with_a_nul_prefix_before_reader_materialization(
        string column,
        int maximumBytes)
    {
        var observer = new ReaderValueObserver();
        await using var fixture = await SqliteFixture.CreateAsync(observer);
        await fixture.InsertRowAsync(Day(2));
        await fixture.SetOversizedValueAsync(column, maximumBytes, multibyte: true);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() => fixture.QueryAsync(pageSize: 1));

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.DoesNotContain(observer.StringValues, value => value.Contains('\0') || value.Contains('\u6570'));
    }

    [Fact]
    public async Task QueryAsync_rejects_an_oversized_lookahead_row_without_returning_its_text()
    {
        var observer = new ReaderValueObserver();
        await using var fixture = await SqliteFixture.CreateAsync(observer);
        await fixture.InsertRowAsync(Day(3));
        var oversizedId = await fixture.InsertRowAsync(Day(2));
        await fixture.SetOversizedValueAsync(
            "security_description",
            ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditEvent.MaxDescriptionLength),
            multibyte: false,
            oversizedId);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() => fixture.QueryAsync(pageSize: 1));

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.DoesNotContain(observer.StringValues, value => value.Contains('\0'));
    }

    [Fact]
    public async Task QueryAsync_applies_the_bounded_projection_to_a_row_changed_after_count()
    {
        var observer = new ReaderValueObserver
        {
            BeforePageRead = command =>
            {
                using var pragma = command.Connection!.CreateCommand();
                pragma.CommandText = "PRAGMA ignore_check_constraints = ON";
                pragma.ExecuteNonQuery();
                using var update = command.Connection!.CreateCommand();
                update.CommandText =
                    "UPDATE service_audit_logs SET security_description = char(0) || printf('%.*c', 20000, 'x');";
                update.ExecuteNonQuery();
            }
        };
        await using var fixture = await SqliteFixture.CreateAsync(observer);
        await fixture.InsertRowAsync(Day(2));

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() => fixture.QueryAsync(pageSize: 1));

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.True(observer.BeforePageReadInvoked);
        Assert.DoesNotContain(observer.StringValues, value => value.Contains('\0'));
    }

    private static DateTimeOffset Day(int day) =>
        new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly AuditTestDbContext context;

        private SqliteFixture(SqliteConnection connection, AuditTestDbContext context)
        {
            this.connection = connection;
            this.context = context;
        }

        internal static async Task<SqliteFixture> CreateAsync(DbCommandInterceptor interceptor)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var context = new AuditTestDbContext(
                new DbContextOptionsBuilder<AuditTestDbContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(interceptor)
                    .Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new SqliteFixture(connection, context);
        }

        internal async Task<string> InsertRowAsync(DateTimeOffset occurredAtUtc)
        {
            var id = Guid.NewGuid().ToString("D");
            const string metadataJson = "{\"reason\":\"safe\"}";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_audit_logs
                    (id, operator_id, operator_display_name, operator_source, action, target_type,
                     target_id, outcome, occurred_at_utc, client_ip, correlation_id,
                     security_description, metadata_json)
                VALUES
                    ({id}, 'admin-1', 'Administrator', 'interactive_admin', 'configuration.changed',
                     'configuration', 'smtp', 1, {occurredAtUtc.UtcDateTime}, '192.0.2.1', 'request-1',
                     'safe description', {metadataJson});
                """,
                TestContext.Current.CancellationToken);
            return id;
        }

        internal async Task SetOversizedValueAsync(
            string column,
            int maximumBytes,
            bool multibyte,
            string? id = null)
        {
            await context.Database.ExecuteSqlRawAsync(
                "PRAGMA ignore_check_constraints = ON",
                TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = multibyte
                ? $"UPDATE service_audit_logs SET {column} = char(0) || replace(hex(zeroblob($length)), '00', char(25968))"
                : $"UPDATE service_audit_logs SET {column} = char(0) || printf('%.*c', $length, 'x')";
            if (id is not null)
            {
                command.CommandText += " WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
            }

            command.Parameters.AddWithValue("$length", maximumBytes + 1);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "PRAGMA ignore_check_constraints = OFF",
                TestContext.Current.CancellationToken);
        }

        internal Task<ManagementAuditQueryResult> QueryAsync(int pageSize) =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(
                    ManagementAuditQuery.Create(pageSize: pageSize),
                    TestContext.Current.CancellationToken)
                .AsTask();

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReaderValueObserver : DbCommandInterceptor
    {
        internal List<string> CommandTexts { get; } = [];

        internal List<string> StringValues { get; } = [];

        internal Action<DbCommand>? BeforePageRead { get; init; }

        internal bool BeforePageReadInvoked { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!BeforePageReadInvoked
                && BeforePageRead is not null
                && command.CommandText.Contains("CASE", StringComparison.OrdinalIgnoreCase))
            {
                BeforePageReadInvoked = true;
                BeforePageRead(command);
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            CommandTexts.Add(command.CommandText);
            return new ObservingDataReader(result, StringValues);
        }
    }

    private sealed class ObservingDataReader(DbDataReader inner, ICollection<string> values) : DbDataReader
    {
        public override object this[int ordinal] => Observe(inner[ordinal]);
        public override object this[string name] => Observe(inner[name]);
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override int VisibleFieldCount => inner.VisibleFieldCount;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => (string)Observe(inner.GetString(ordinal));
        public override object GetValue(int ordinal) => Observe(inner.GetValue(ordinal));
        public override int GetValues(object[] valuesBuffer)
        {
            var count = inner.GetValues(valuesBuffer);
            for (var index = 0; index < count; index++)
            {
                valuesBuffer[index] = Observe(valuesBuffer[index]);
            }

            return count;
        }
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
            inner.NextResultAsync(cancellationToken);
        public override bool Read() => inner.Read();
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
        public override T GetFieldValue<T>(int ordinal) => (T)Observe(inner.GetFieldValue<T>(ordinal));
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
            ObserveAsync(inner.GetFieldValueAsync<T>(ordinal, cancellationToken));
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override void Close() => inner.Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        private T Observe<T>(T value)
        {
            if (value is string text)
            {
                values.Add(text);
            }

            return value;
        }

        private async Task<T> ObserveAsync<T>(Task<T> task) =>
            Observe(await task.ConfigureAwait(false));
    }
}

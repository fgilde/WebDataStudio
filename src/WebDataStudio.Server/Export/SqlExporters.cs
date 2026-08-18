using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;

namespace WebDataStudio.Server.Export;

/// Renders values as SQL literals. Shared by the exporters here and by P4's change-script builder,
/// so the preview a user approves and the script that runs can never drift apart.
public static class SqlLiteral
{
    public static string Render(object? value, SqlDialect dialect) => value switch
    {
        null => "NULL",
        bool b => dialect is Drivers.SqlServer.SqlServerDialect ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE"),
        byte or sbyte or short or ushort or int or uint or long or ulong
             or float or double or decimal => Number(value),
        DateTime dt => dialect.QuoteLiteral(dt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
        DateTimeOffset dto => dialect.QuoteLiteral(dto.ToString("O")),
        _ => dialect.QuoteLiteral(value.ToString() ?? ""),
    };

    private static string Number(object value) =>
        ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
}

public sealed class SqlInsertExporter : IResultExporter
{
    // Batching keeps the script readable and stays well inside every engine's statement limit.
    private const int RowsPerStatement = 500;

    public string Format => "sql-insert";
    public string Label => "SQL (INSERT)";
    public string ContentType => "application/sql";
    public string FileExtension => "sql";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        var dialect = options.Dialect ?? new PostgreSqlDialect();
        var table = options.TableName ?? "exported_rows";

        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        IReadOnlyList<ColumnMeta> columns = [];
        var pending = new List<object?[]>();

        async Task FlushAsync()
        {
            if (pending.Count == 0 || columns.Count == 0) return;

            var names = string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)));
            await writer.WriteLineAsync($"INSERT INTO {dialect.QuoteIdentifier(table)} ({names}) VALUES");

            for (var i = 0; i < pending.Count; i++)
            {
                var values = string.Join(", ", pending[i].Select(v => SqlLiteral.Render(v, dialect)));
                await writer.WriteLineAsync($"  ({values}){(i == pending.Count - 1 ? ";" : ",")}");
            }

            pending.Clear();
            await writer.FlushAsync(ct);
        }

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items;
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                    {
                        pending.Add(row);
                        if (pending.Count >= RowsPerStatement) await FlushAsync();
                    }
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await FlushAsync();
        await writer.FlushAsync(ct);
    }
}

/// CREATE TABLE followed by the inserts, so a result can be recreated somewhere else.
public sealed class SqlSchemaExporter : IResultExporter
{
    public string Format => "sql-create";
    public string Label => "SQL (CREATE + INSERT)";
    public string ContentType => "application/sql";
    public string FileExtension => "sql";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        var dialect = options.Dialect ?? new PostgreSqlDialect();
        var table = options.TableName ?? "exported_rows";

        // The CREATE has to be written before the rows stream past, so it is built from the column
        // metadata alone — the source type name, mapped where we know it, passed through otherwise.
        var buffer = new MemoryStream();
        var inserts = new SqlInsertExporter();

        IReadOnlyList<ColumnMeta> columns = [];
        var seen = new TaskCompletionSource<IReadOnlyList<ColumnMeta>>();

        async IAsyncEnumerable<ResultChunk> Tee()
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (chunk is ResultChunk.Columns c && !seen.Task.IsCompleted) seen.SetResult(c.Items);
                yield return chunk;
            }
            if (!seen.Task.IsCompleted) seen.SetResult([]);
        }

        await inserts.WriteAsync(buffer, Tee(), options, ct);
        columns = await seen.Task;

        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        if (columns.Count > 0)
        {
            await writer.WriteLineAsync($"CREATE TABLE {dialect.QuoteIdentifier(table)} (");
            for (var i = 0; i < columns.Count; i++)
            {
                var comma = i == columns.Count - 1 ? "" : ",";
                await writer.WriteLineAsync(
                    $"  {dialect.QuoteIdentifier(columns[i].Name)} {TypeFor(columns[i], dialect)}{comma}");
            }
            await writer.WriteLineAsync(");");
            await writer.WriteLineAsync();
        }

        await writer.FlushAsync(ct);
        buffer.Position = 0;
        await buffer.CopyToAsync(target, ct);
    }

    private static string TypeFor(ColumnMeta column, SqlDialect dialect)
    {
        var source = column.DataType.ToLowerInvariant();

        var neutral = source switch
        {
            "int" or "int4" or "integer" or "serial" => "int",
            "bigint" or "int8" or "bigserial" => "bigint",
            "smallint" or "int2" => "smallint",
            "bool" or "boolean" or "bit" => "bool",
            "real" or "float4" or "float" => "float",
            "double" or "float8" or "double precision" => "double",
            "numeric" or "decimal" or "money" => "decimal",
            "date" => "date",
            "timestamp" or "datetime" or "datetime2" or "timestamptz" => "timestamp",
            "uuid" or "uniqueidentifier" => "uuid",
            "json" or "jsonb" => "json",
            "blob" or "bytea" or "varbinary" => "blob",
            _ => "text",
        };

        return (dialect, neutral) switch
        {
            (Drivers.SqlServer.SqlServerDialect, "text") => "NVARCHAR(MAX)",
            (Drivers.SqlServer.SqlServerDialect, "bool") => "BIT",
            (Drivers.SqlServer.SqlServerDialect, "timestamp") => "DATETIME2",
            (Drivers.SqlServer.SqlServerDialect, "uuid") => "UNIQUEIDENTIFIER",
            (Drivers.SqlServer.SqlServerDialect, "json") => "NVARCHAR(MAX)",
            (Drivers.SqlServer.SqlServerDialect, "blob") => "VARBINARY(MAX)",
            (Drivers.SqlServer.SqlServerDialect, "double") => "FLOAT",

            (Drivers.MySql.MySqlDialect, "text") => "TEXT",
            (Drivers.MySql.MySqlDialect, "bool") => "TINYINT(1)",
            (Drivers.MySql.MySqlDialect, "uuid") => "CHAR(36)",
            (Drivers.MySql.MySqlDialect, "blob") => "BLOB",
            (Drivers.MySql.MySqlDialect, "double") => "DOUBLE",

            (Drivers.Sqlite.SqliteDialect, "bool") => "INTEGER",
            (Drivers.Sqlite.SqliteDialect, "timestamp") => "TEXT",
            (Drivers.Sqlite.SqliteDialect, "uuid") => "TEXT",
            (Drivers.Sqlite.SqliteDialect, "json") => "TEXT",
            (Drivers.Sqlite.SqliteDialect, "double") => "REAL",
            (Drivers.Sqlite.SqliteDialect, "int") => "INTEGER",
            (Drivers.Sqlite.SqliteDialect, "bigint") => "INTEGER",

            (_, "bool") => "BOOLEAN",
            (_, "double") => "DOUBLE PRECISION",
            (_, "uuid") => "UUID",
            (_, "json") => "JSONB",
            (_, "blob") => "BYTEA",
            (_, "int") => "INTEGER",
            _ => neutral.ToUpperInvariant(),
        };
    }
}

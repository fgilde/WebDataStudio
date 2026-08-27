using System.Data.Common;
using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One place the value turned up.
public sealed record DataHit(string Schema, string Table, string Column, string DataType, long Matches);

public sealed record DataSearchResult(
    IReadOnlyList<DataHit> Hits,
    int TablesSearched,
    int TablesSkipped,
    /// Tables that could not be searched, with the reason — a permission, a type nothing casts to.
    IReadOnlyList<string> Notes,
    bool Truncated);

/// "Find 4711 in any table."
///
/// The object search answers "where is the table called orders"; this answers the other question,
/// which is the one somebody asks at four in the afternoon with a support ticket open. It runs on the
/// server, one query per table, and it is type-aware: a number is compared against numeric columns as
/// a number and only looked for inside text where that makes sense, so searching for 42 does not scan
/// every timestamp in the database.
public static class DataSearch
{
    /// A whole database of tables is a lot of table scans. The cap is generous and the answer says
    /// when it was reached, which beats a search that quietly stops early.
    public const int DefaultMaxTables = 200;

    public static async Task<DataSearchResult> RunAsync(
        IDbDriver driver, IDbSession session, string value, string? schema, bool exact,
        int maxTables, int timeoutSeconds, CancellationToken ct)
    {
        if (value.Trim().Length == 0)
            return new DataSearchResult([], 0, 0, ["nothing to search for"], false);

        var columns = await ColumnsAsync(driver, session, schema, ct);
        var tables = columns
            .GroupBy(column => (column.Schema, column.Table))
            .Take(Math.Clamp(maxTables, 1, 5000) + 1)
            .ToList();

        var truncated = tables.Count > Math.Clamp(maxTables, 1, 5000);
        if (truncated) tables.RemoveAt(tables.Count - 1);

        var hits = new List<DataHit>();
        var notes = new List<string>();
        var searched = 0;
        var skipped = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            var candidates = table.Where(column => Comparable(column.DataType, value)).ToList();

            if (candidates.Count == 0)
            {
                // No column in this table could hold the value: not searched, and not a failure.
                skipped++;
                continue;
            }

            try
            {
                hits.AddRange(await SearchTableAsync(
                    driver, session, table.Key.Schema, table.Key.Table, candidates, value, exact,
                    timeoutSeconds, ct));

                searched++;
            }
            catch (DbException e)
            {
                skipped++;
                notes.Add($"{Name(table.Key.Schema, table.Key.Table)}: {e.Message}");
            }
        }

        return new DataSearchResult(
            hits.OrderByDescending(hit => hit.Matches).ToList(), searched, skipped, notes, truncated);
    }

    private sealed record ColumnRef(string Schema, string Table, string Column, string DataType);

    /// Every column the search could look at, in one round trip. A describe per table would be
    /// hundreds of queries before the first row is compared.
    private static async Task<IReadOnlyList<ColumnRef>> ColumnsAsync(
        IDbDriver driver, IDbSession session, string? schema, CancellationToken ct)
    {
        var filter = schema is { Length: > 0 } ? schema.Replace("'", "''") : null;

        var sql = driver.Info.Id switch
        {
            "postgresql" or "mysql" or "clickhouse" or "duckdb" => $"""
                SELECT table_schema, table_name, column_name, data_type
                  FROM information_schema.columns
                 WHERE {(filter is null
                     ? "table_schema NOT IN ('pg_catalog', 'information_schema', 'sys', 'mysql', 'performance_schema')"
                     : $"table_schema = '{filter}'")}
                 ORDER BY table_schema, table_name, ordinal_position
                """,
            "sqlserver" => $"""
                SELECT s.name, t.name, c.name, ty.name
                  FROM sys.columns c
                  JOIN sys.tables t ON t.object_id = c.object_id
                  JOIN sys.schemas s ON s.schema_id = t.schema_id
                  JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                 {(filter is null ? "" : $"WHERE s.name = '{filter}'")}
                 ORDER BY s.name, t.name, c.column_id
                """,
            "sqlite" => """
                SELECT 'main', m.name, p.name, p.type
                  FROM sqlite_master m
                  JOIN pragma_table_info(m.name) p
                 WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
                """,
            "oracle" => $"""
                SELECT owner, table_name, column_name, data_type
                  FROM all_tab_columns
                 WHERE {(filter is null ? "owner = USER" : $"owner = '{filter.ToUpperInvariant()}'")}
                 ORDER BY owner, table_name, column_id
                """,
            _ => null,
        };

        if (sql is null) return [];

        var columns = new List<ColumnRef>();

        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(new ColumnRef(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3).ToLowerInvariant()));

        return columns;
    }

    /// One query per table and therefore one scan, with a count per candidate column.
    private static async Task<IReadOnlyList<DataHit>> SearchTableAsync(
        IDbDriver driver, IDbSession session, string schema, string table,
        IReadOnlyList<ColumnRef> columns, string value, bool exact, int timeoutSeconds,
        CancellationToken ct)
    {
        var dialect = driver.Dialect;
        var parameter = dialect.ParameterPrefix + "needle";
        var numeric = dialect.ParameterPrefix + "number";

        var counts = columns.Select((column, index) =>
        {
            var quoted = dialect.QuoteIdentifier(column.Column);

            // A number against a numeric column is compared as a number; everything else is compared
            // as text, which is what "find 4711 anywhere" means.
            var text = $"LOWER(CAST({quoted} AS {dialect.TextType}))";

            var condition = IsNumeric(column.DataType)
                ? $"{quoted} = {numeric}"
                // Without case, on every engine: PostgreSQL's LIKE is case-sensitive and MySQL's is
                // not, and a search that finds different rows per connection is worse than useless.
                : exact ? $"{text} = {parameter}" : $"{text} LIKE {parameter}";

            return $"SUM(CASE WHEN {condition} THEN 1 ELSE 0 END) AS c{index}";
        });

        await using var command = session.Connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", counts)} FROM {Qualify(dialect, schema, table)}";
        command.CommandTimeout = timeoutSeconds;

        Add(command, parameter, (exact ? value : $"%{value}%").ToLowerInvariant());

        if (columns.Any(column => IsNumeric(column.DataType))
            && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            Add(command, numeric, number);
        else if (columns.Any(column => IsNumeric(column.DataType)))
            Add(command, numeric, decimal.MinValue);

        var hits = new List<DataHit>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return hits;

        for (var index = 0; index < columns.Count; index++)
        {
            if (reader.IsDBNull(index)) continue;

            var matches = Convert.ToInt64(reader.GetValue(index));
            if (matches <= 0) continue;

            hits.Add(new DataHit(schema, table, columns[index].Column, columns[index].DataType, matches));
        }

        return hits;
    }

    /// Whether this column could hold the value at all. This is what keeps the search from casting
    /// every timestamp in the database to text.
    private static bool Comparable(string dataType, string value)
    {
        if (IsNumeric(dataType))
            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        if (IsTemporal(dataType))
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        // Binary, geometry and the like: a text comparison against them means nothing.
        return !IsUnsearchable(dataType);
    }

    private static bool IsNumeric(string dataType) =>
        dataType.Contains("int") || dataType.Contains("numeric") || dataType.Contains("decimal")
        || dataType.Contains("real") || dataType.Contains("double") || dataType.Contains("float")
        || dataType.Contains("money") || dataType.Contains("number");

    private static bool IsTemporal(string dataType) =>
        dataType.Contains("date") || dataType.Contains("time");

    private static bool IsUnsearchable(string dataType) =>
        dataType.Contains("blob") || dataType.Contains("binary") || dataType.Contains("image")
        || dataType.Contains("geometry") || dataType.Contains("geography")
        || dataType.Contains("bytea") || dataType.Contains("raw");

    private static string Qualify(SqlDialect dialect, string schema, string table) =>
        schema.Length == 0
            ? dialect.QuoteIdentifier(table)
            : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";

    private static string Name(string schema, string table) =>
        schema.Length == 0 ? table : $"{schema}.{table}";

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

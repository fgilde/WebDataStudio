using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One version of a row, as the database kept it.
public sealed record RowVersion(
    /// When this version started being the truth, where the engine records it.
    string? From,
    /// When it stopped. Null on the version that is current.
    string? To,
    IReadOnlyList<object?> Values,
    /// Which columns differ from the version before this one — the whole point of the list.
    IReadOnlyList<string> Changed);

public sealed record RowHistoryResult(
    bool Supported,
    IReadOnlyList<ColumnMeta> Columns,
    IReadOnlyList<RowVersion> Versions,
    /// Why there is nothing, when there is nothing.
    string? Note);

/// "What did this row look like yesterday?"
///
/// Only where the database itself kept the answer. SQL Server keeps it for a system-versioned
/// table, MariaDB for one with system versioning, Oracle for as long as its undo retention reaches
/// back. PostgreSQL, MySQL, SQLite and the rest keep nothing of the sort, and this says so rather
/// than inventing a history out of an audit trail that only covers what went through the studio.
public static class RowHistory
{
    /// How many versions are worth reading. A row somebody has been updating in a loop has
    /// thousands, and the interesting ones are the recent ones.
    public const int MaxVersions = 100;

    /// Whether this table keeps its own history. One catalogue lookup, asked when a data tab opens
    /// rather than for every page.
    public static async Task<(bool Supported, string? Note)> SupportsAsync(IDbDriver driver,
        IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        switch (driver.Info.Id)
        {
            case "sqlserver":
            {
                var sql = """
                    SELECT t.temporal_type
                      FROM sys.tables t
                      JOIN sys.schemas s ON s.schema_id = t.schema_id
                     WHERE s.name = @schema AND t.name = @name
                    """;

                var value = await ScalarAsync(session, sql, Parameters(target, "@"), ct);

                // 2 is SYSTEM_VERSIONED_TEMPORAL_TABLE; 0 and 1 are a plain table and a history one.
                return Convert.ToInt32(value ?? 0) == 2
                    ? (true, null)
                    : (false, "this table is not system-versioned, so the database kept no history of it");
            }

            case "mysql":
            {
                // MariaDB only: a versioned table has the two generated period columns.
                var sql = """
                    SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = @schema AND table_name = @name
                       AND extra LIKE '%ROW %'
                    """;

                var value = await ScalarAsync(session, sql, Parameters(target, "@"), ct);

                return Convert.ToInt32(value ?? 0) >= 2
                    ? (true, null)
                    : (false, "this table has no system versioning (MariaDB keeps one; MySQL has none)");
            }

            case "oracle":
                // Flashback reads the undo tablespace, so it is not a property of the table: how far
                // back it reaches depends on the server, and saying that is more honest than a flag.
                return (true, "Oracle answers from its undo tablespace, so how far back this reaches "
                              + "depends on the server's retention");

            default:
                return (false, $"{driver.Info.Label} keeps no row history of its own");
        }
    }

    /// The versions of one row, newest first.
    public static async Task<RowHistoryResult> ReadAsync(IDbDriver driver, IDbSession session,
        SchemaNodeRef target, IReadOnlyDictionary<string, string?> key, int limit, CancellationToken ct)
    {
        var (supported, note) = await SupportsAsync(driver, session, target, ct);
        if (!supported) return new RowHistoryResult(false, [], [], note);

        // A row addressed by where it physically is has no history to follow: the address is what
        // changes when the row is written, so the versions of it are not versions of one row.
        if (key.Count == 0 || key.ContainsKey(Editing.RowIdentity.AddressColumn))
            return new RowHistoryResult(true, [], [],
                "this row has no key columns, so there is no way to follow one row through time");

        var table = driver.FromClause(session, target)
                    ?? driver.Dialect.QuoteIdentifier(target.Name);

        var where = string.Join(" AND ", key.Keys
            .Select((column, index) => $"{driver.Dialect.QuoteIdentifier(column)} = {driver.Dialect.ParameterPrefix}k{index}"));

        var sql = driver.Info.Id switch
        {
            "sqlserver" =>
                $"SELECT TOP {limit} * FROM {table} FOR SYSTEM_TIME ALL WHERE {where} "
                + "ORDER BY 1 DESC",

            "mysql" =>
                $"SELECT * FROM {table} FOR SYSTEM_TIME ALL WHERE {where} LIMIT {limit}",

            "oracle" =>
                $"SELECT * FROM {table} VERSIONS BETWEEN TIMESTAMP MINVALUE AND MAXVALUE "
                + $"WHERE {where} FETCH FIRST {limit} ROWS ONLY",

            _ => null,
        };

        if (sql is null) return new RowHistoryResult(false, [], [], note);

        var parameters = key.Keys
            .Select((column, index) => (Name: $"k{index}", Value: key[column]))
            .ToDictionary(entry => entry.Name, entry => entry.Value);

        var columns = new List<ColumnMeta>();
        var rows = new List<object?[]>();
        string? failure = null;

        await foreach (var chunk in driver.ExecuteAsync(session,
            new ScriptRequest(sql, limit, 60, Parameters: parameters), ct))
            switch (chunk)
            {
                case ResultChunk.Columns c: columns = [.. c.Items]; break;
                case ResultChunk.Rows r: rows.AddRange(r.Items); break;
                case ResultChunk.Error e: failure = e.Text; break;
            }

        if (failure is not null) return new RowHistoryResult(true, [], [], failure);

        return new RowHistoryResult(true, columns, Versions(columns, rows, driver.Info.Id), note);
    }

    /// The rows as versions, each one carrying what changed since the one before it. Reading a list
    /// of twenty near-identical rows is what this saves.
    private static List<RowVersion> Versions(IReadOnlyList<ColumnMeta> columns,
        IReadOnlyList<object?[]> rows, string engine)
    {
        var periodStart = columns.ToList().FindIndex(column => IsPeriod(column.Name, start: true));
        var periodEnd = columns.ToList().FindIndex(column => IsPeriod(column.Name, start: false));

        var versions = new List<RowVersion>();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var previous = index + 1 < rows.Count ? rows[index + 1] : null;

            var changed = previous is null
                ? []
                : columns
                    .Select((column, position) => (column.Name, position))
                    .Where(entry => !Equals(row[entry.position], previous[entry.position]))
                    .Select(entry => entry.Name)
                    .ToList();

            versions.Add(new RowVersion(
                periodStart >= 0 ? row[periodStart]?.ToString() : null,
                periodEnd >= 0 ? row[periodEnd]?.ToString() : null,
                row, changed));
        }

        _ = engine;
        return versions;
    }

    /// The period columns each engine generates. Their names are the deployment's own on SQL Server
    /// and MariaDB, so what is recognised is the usual spelling — and where it is not recognised the
    /// version simply carries no dates, which is a smaller loss than a wrong one.
    private static bool IsPeriod(string name, bool start)
    {
        var lower = name.ToLowerInvariant();

        return start
            ? lower is "validfrom" or "valid_from" or "sysstarttime" or "row_start" or "versions_starttime"
            : lower is "validto" or "valid_to" or "sysendtime" or "row_end" or "versions_endtime";
    }

    private static Dictionary<string, object?> Parameters(SchemaNodeRef target, string prefix) =>
        new()
        {
            [$"{prefix}schema"] = target.Path.Count > 1 ? target.Path[0] : "",
            [$"{prefix}name"] = target.Name,
        };

    private static async Task<object?> ScalarAsync(IDbSession session, string sql,
        Dictionary<string, object?> parameters, CancellationToken ct)
    {
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            return await command.ExecuteScalarAsync(ct);
        }
        catch (DbException)
        {
            // A catalogue this connection may not read is not an error worth showing: it means the
            // studio cannot tell, and "no history" is the safe answer.
            return null;
        }
    }
}

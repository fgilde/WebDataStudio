using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Admin;

public sealed record SystemCommand(
    string Id, string Label, string Sql, bool NeedsTarget, bool Destructive, string Description);

public sealed record SessionEntry(string Id, string User, string Database, string Query,
    string State, long DurationMs, string? BlockedBy);

public sealed record DatabaseEntry(string Name, long? SizeBytes);

/// An allow-list, never a passthrough. Anything the user can run here is spelled out, so this
/// endpoint cannot become a second, unlogged query console.
public static class SystemCommandCatalog
{
    private static readonly Dictionary<string, SystemCommand[]> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgresql"] =
        [
            new("vacuum", "VACUUM", "VACUUM (ANALYZE) {target}", true, false,
                "Reclaims dead tuples and refreshes statistics."),
            new("analyze", "ANALYZE", "ANALYZE {target}", true, false,
                "Refreshes planner statistics."),
            new("reindex", "REINDEX", "REINDEX TABLE {target}", true, false,
                "Rebuilds the indexes of a table."),
            new("vacuum-full", "VACUUM FULL", "VACUUM FULL {target}", true, true,
                "Rewrites the table; takes an exclusive lock for the whole run."),
        ],
        ["mysql"] =
        [
            new("optimize", "OPTIMIZE TABLE", "OPTIMIZE TABLE {target}", true, false,
                "Reclaims unused space and defragments the table."),
            new("analyze", "ANALYZE TABLE", "ANALYZE TABLE {target}", true, false,
                "Refreshes index statistics."),
            new("check", "CHECK TABLE", "CHECK TABLE {target}", true, false,
                "Checks the table for errors."),
            new("flush", "FLUSH TABLES", "FLUSH TABLES", false, false,
                "Closes open tables and flushes the table cache."),
        ],
        ["sqlserver"] =
        [
            new("checkdb", "DBCC CHECKDB", "DBCC CHECKDB", false, false,
                "Checks the logical and physical integrity of the database."),
            new("update-statistics", "UPDATE STATISTICS", "UPDATE STATISTICS {target}", true, false,
                "Refreshes statistics for a table."),
            new("rebuild-indexes", "ALTER INDEX ALL REBUILD", "ALTER INDEX ALL ON {target} REBUILD", true, false,
                "Rebuilds every index on a table."),
            new("shrink-log", "DBCC SHRINKFILE", "DBCC SHRINKFILE (2)", false, true,
                "Shrinks the transaction log; fragmenting it is a real cost."),
        ],
        ["sqlite"] =
        [
            new("vacuum", "VACUUM", "VACUUM", false, false, "Rebuilds the database file compactly."),
            new("analyze", "ANALYZE", "ANALYZE", false, false, "Collects statistics for the planner."),
            new("integrity-check", "PRAGMA integrity_check", "PRAGMA integrity_check", false, false,
                "Checks the database file for corruption."),
        ],
        ["clickhouse"] =
        [
            new("optimize", "OPTIMIZE TABLE", "OPTIMIZE TABLE {target} FINAL", true, false,
                "Merges parts; on a large table this is expensive."),
        ],
        ["duckdb"] =
        [
            new("checkpoint", "CHECKPOINT", "CHECKPOINT", false, false,
                "Flushes the write-ahead log into the database file."),
            new("analyze", "ANALYZE", "ANALYZE", false, false, "Collects statistics for the planner."),
        ],
        ["oracle"] =
        [
            new("gather-stats", "Gather statistics",
                "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, '{target}'); END;", true, false,
                "Refreshes optimizer statistics for a table."),
        ],
    };

    public static IReadOnlyList<SystemCommand> For(string engine) =>
        Commands.TryGetValue(engine, out var commands) ? commands : [];

    /// Substitutes the target through the dialect's quoting; the caller never supplies raw SQL.
    public static string Render(SystemCommand command, string? target, SqlDialect dialect)
    {
        if (!command.NeedsTarget) return command.Sql;
        if (target is not { Length: > 0 })
            throw new InvalidOperationException($"{command.Label} needs a target table");

        var quoted = command.Sql.Contains("'{target}'")
            ? target.Replace("'", "''")
            : string.Join(".", target.Split('.').Select(dialect.QuoteIdentifier));

        return command.Sql.Replace("{target}", quoted);
    }
}

public static class SessionService
{
    public static async Task<IReadOnlyList<SessionEntry>> ListAsync(IDbDriver driver, IDbSession session,
        CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            "postgresql" => """
                SELECT pid::text, usename, datname, coalesce(query, ''), state,
                       coalesce(EXTRACT(milliseconds FROM (now() - query_start))::bigint, 0),
                       (SELECT string_agg(b::text, ',') FROM unnest(pg_blocking_pids(pid)) b)
                  FROM pg_stat_activity
                 WHERE pid <> pg_backend_pid()
                """,
            "mysql" => """
                SELECT CAST(id AS CHAR), user, coalesce(db, ''), coalesce(info, ''), command,
                       time * 1000, NULL
                  FROM information_schema.processlist
                 WHERE id <> CONNECTION_ID()
                """,
            "sqlserver" => """
                SELECT CAST(s.session_id AS varchar), s.login_name, DB_NAME(s.database_id),
                       COALESCE(t.text, ''), s.status, COALESCE(r.total_elapsed_time, 0),
                       NULLIF(CAST(r.blocking_session_id AS varchar), '0')
                  FROM sys.dm_exec_sessions s
                  LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                  OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                 WHERE s.session_id <> @@SPID AND s.is_user_process = 1
                """,
            _ => null,
        };

        if (sql is null) return [];

        var entries = new List<SessionEntry>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(new SessionEntry(
                    reader.GetValue(0).ToString() ?? "",
                    reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "",
                    reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString() ?? "",
                    reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString() ?? "",
                    reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString() ?? "",
                    reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5)),
                    reader.IsDBNull(6) ? null : reader.GetValue(6).ToString()));
        }
        catch (DbException)
        {
            // No permission to see other sessions: an empty list, not a failed panel.
        }

        return entries;
    }

    public static async Task<bool> KillAsync(IDbDriver driver, IDbSession session, string id,
        CancellationToken ct)
    {
        if (!driver.Caps.KillSession) throw new NotSupportedException("this engine cannot kill a session");
        if (!long.TryParse(id, out var numeric))
            throw new InvalidOperationException("a session id must be numeric");

        var sql = driver.Info.Id switch
        {
            "postgresql" => $"SELECT pg_terminate_backend({numeric})",
            "mysql" => $"KILL {numeric}",
            "sqlserver" => $"KILL {numeric}",
            _ => null,
        };

        if (sql is null) return false;

        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }
}

public static class DatabaseAdmin
{
    public static async Task<IReadOnlyList<DatabaseEntry>> ListAsync(IDbDriver driver, IDbSession session,
        CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            "postgresql" =>
                "SELECT datname, pg_database_size(datname) FROM pg_database WHERE NOT datistemplate ORDER BY datname",
            "mysql" =>
                "SELECT schema_name, NULL FROM information_schema.schemata ORDER BY schema_name",
            "sqlserver" =>
                "SELECT name, CAST(NULL AS bigint) FROM sys.databases ORDER BY name",
            "clickhouse" =>
                "SELECT name, NULL FROM system.databases ORDER BY name",
            _ => null,
        };

        if (sql is null) return [];

        var entries = new List<DatabaseEntry>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(new DatabaseEntry(reader.GetString(0),
                    reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1))));
        }
        catch (DbException)
        {
            // Same reasoning as above: a missing permission is an empty list.
        }

        return entries;
    }

    public static async Task CreateAsync(IDbDriver driver, IDbSession session, string name, CancellationToken ct)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {driver.Dialect.QuoteIdentifier(name)}";
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task DropAsync(IDbDriver driver, IDbSession session, string name, CancellationToken ct)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = $"DROP DATABASE {driver.Dialect.QuoteIdentifier(name)}";
        await command.ExecuteNonQueryAsync(ct);
    }
}

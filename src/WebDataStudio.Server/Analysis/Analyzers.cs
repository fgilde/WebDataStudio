using System.Data.Common;
using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// Shared plumbing for the per-engine deep-analyze queries: run a statement, turn each row into a
/// finding. Output is deterministic — no timestamps, stable ordering — so running twice reads the
/// same, which is what makes the report trustworthy.
public static class AnalyzerSupport
{
    public static async Task<List<AnalyzeFinding>> QueryAsync(IDbSession session, string sql,
        Func<DbDataReader, AnalyzeFinding> map, CancellationToken ct)
    {
        var findings = new List<AnalyzeFinding>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) findings.Add(map(reader));
        }
        catch (DbException)
        {
            // A catalogue view the role cannot read is a missing answer, not a failed report.
        }

        return findings;
    }

    public static AnalyzeReport Sorted(IEnumerable<AnalyzeFinding> findings) =>
        new(findings
            .OrderBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList());
}

public static class PostgreSqlAnalyzer
{
    public static async Task<AnalyzeReport> RunAsync(IDbSession session, string? schema, CancellationToken ct)
    {
        var filter = schema is { Length: > 0 } ? $" AND n.nspname = '{schema.Replace("'", "''")}'" : "";
        var findings = new List<AnalyzeFinding>();

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT s.relname, s.indexrelname, pg_relation_size(s.indexrelid)
              FROM pg_stat_user_indexes s
              JOIN pg_class c ON c.oid = s.indexrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_index i ON i.indexrelid = s.indexrelid
             WHERE s.idx_scan = 0 AND NOT i.indisprimary AND NOT i.indisunique{filter}
             ORDER BY s.relname, s.indexrelname
            """,
            r => new AnalyzeFinding("unused-index", "info",
                $"Unused index {r.GetString(1)}",
                $"{r.GetString(1)} on {r.GetString(0)} has never been used since the last statistics " +
                $"reset and occupies {r.GetInt64(2) / 1024} KiB.",
                $"DROP INDEX {r.GetString(1)};"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT t.relname, string_agg(i.relname, ', ' ORDER BY i.relname), ix.indkey::text
              FROM pg_index ix
              JOIN pg_class i ON i.oid = ix.indexrelid
              JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE true{filter}
             GROUP BY t.relname, ix.indrelid, ix.indkey
            HAVING count(*) > 1
             ORDER BY t.relname
            """,
            r => new AnalyzeFinding("duplicate-index", "warning",
                $"Duplicate indexes on {r.GetString(0)}",
                $"These indexes cover the same columns: {r.GetString(1)}. Every write maintains all of them.",
                // The statement drops all but the first: a finding without the fix in it is a
                // finding somebody has to translate by hand.
                DropAllButFirst(r.GetString(1))), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT t.relname, con.conname,
                   (SELECT string_agg(att.attname, ', ')
                      FROM unnest(con.conkey) k
                      JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k)
              FROM pg_constraint con
              JOIN pg_class t ON t.oid = con.conrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE con.contype = 'f'{filter}
               AND NOT EXISTS (
                   SELECT 1 FROM pg_index ix
                    WHERE ix.indrelid = con.conrelid
                      AND (ix.indkey::int2[])[0:array_length(con.conkey,1)-1] = con.conkey)
             ORDER BY t.relname, con.conname
            """,
            r => new AnalyzeFinding("unindexed-foreign-key", "warning",
                $"Unindexed foreign key {r.GetString(1)}",
                $"{r.GetString(0)}({r.GetString(2)}) references another table but has no index, " +
                "so deletes on the parent scan this table.",
                $"CREATE INDEX ON {r.GetString(0)} ({r.GetString(2)});"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT s.relname, s.n_dead_tup, s.n_live_tup
              FROM pg_stat_user_tables s
              JOIN pg_class c ON c.oid = s.relid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE s.n_dead_tup > 1000 AND s.n_dead_tup > s.n_live_tup * 0.2{filter}
             ORDER BY s.relname
            """,
            r => new AnalyzeFinding("bloat", "warning",
                $"Table bloat in {r.GetString(0)}",
                $"{r.GetInt64(1).ToString("N0", CultureInfo.InvariantCulture)} dead tuples against " +
                $"{r.GetInt64(2).ToString("N0", CultureInfo.InvariantCulture)} live ones.",
                $"VACUUM (ANALYZE) {r.GetString(0)};"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT s.relname
              FROM pg_stat_user_tables s
              JOIN pg_class c ON c.oid = s.relid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE (s.last_analyze IS NULL AND s.last_autoanalyze IS NULL)
               AND s.n_live_tup > 1000{filter}
             ORDER BY s.relname
            """,
            r => new AnalyzeFinding("stale-statistics", "info",
                $"{r.GetString(0)} has never been analyzed",
                "The planner is guessing row counts for this table.",
                $"ANALYZE {r.GetString(0)};"), ct));

        return AnalyzerSupport.Sorted(findings);
    }

    /// The redundant half of a duplicate-index group: keep the first, drop the rest. Which one to
    /// keep is arbitrary, and saying so is better than leaving the finding without a fix.
    private static string? DropAllButFirst(string indexList)
    {
        var indexes = indexList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (indexes.Length < 2) return null;

        return string.Join("\n", indexes.Skip(1).Select(index => $"DROP INDEX {index};"));
    }
}

public static class MySqlAnalyzer
{
    public static async Task<AnalyzeReport> RunAsync(IDbSession session, string? schema, CancellationToken ct)
    {
        var filter = schema is { Length: > 0 } ? $"'{schema.Replace("'", "''")}'" : "DATABASE()";
        var findings = new List<AnalyzeFinding>();

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT object_name, index_name
              FROM sys.schema_unused_indexes
             WHERE object_schema = {filter}
             ORDER BY object_name, index_name
            """,
            r => new AnalyzeFinding("unused-index", "info",
                $"Unused index {r.GetString(1)}",
                $"{r.GetString(1)} on {r.GetString(0)} has not been used since the server started.",
                $"DROP INDEX `{r.GetString(1)}` ON `{r.GetString(0)}`;"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT table_name, GROUP_CONCAT(DISTINCT index_name ORDER BY index_name), columns
              FROM (
                  SELECT table_name, index_name,
                         GROUP_CONCAT(column_name ORDER BY seq_in_index) AS columns
                    FROM information_schema.statistics
                   WHERE table_schema = {filter}
                   GROUP BY table_name, index_name
              ) x
             GROUP BY table_name, columns
            HAVING COUNT(*) > 1
             ORDER BY table_name
            """,
            r => new AnalyzeFinding("duplicate-index", "warning",
                $"Duplicate indexes on {r.GetString(0)}",
                $"These indexes cover ({r.GetString(2)}): {r.GetString(1)}.",
                null), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT k.table_name, k.constraint_name, k.column_name
              FROM information_schema.key_column_usage k
             WHERE k.table_schema = {filter} AND k.referenced_table_name IS NOT NULL
               AND NOT EXISTS (
                   SELECT 1 FROM information_schema.statistics s
                    WHERE s.table_schema = k.table_schema AND s.table_name = k.table_name
                      AND s.column_name = k.column_name AND s.seq_in_index = 1)
             ORDER BY k.table_name, k.constraint_name
            """,
            r => new AnalyzeFinding("unindexed-foreign-key", "warning",
                $"Unindexed foreign key {r.GetString(1)}",
                $"{r.GetString(0)}({r.GetString(2)}) has no leading index.",
                $"CREATE INDEX `ix_{r.GetString(0)}_{r.GetString(2)}` ON `{r.GetString(0)}` (`{r.GetString(2)}`);"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT table_name, data_free
              FROM information_schema.tables
             WHERE table_schema = {filter} AND data_free > 100 * 1024 * 1024
             ORDER BY table_name
            """,
            r => new AnalyzeFinding("bloat", "info",
                $"Reclaimable space in {r.GetString(0)}",
                $"{r.GetInt64(1) / (1024 * 1024)} MiB is allocated but unused.",
                $"OPTIMIZE TABLE `{r.GetString(0)}`;"), ct));

        return AnalyzerSupport.Sorted(findings);
    }
}

public static class SqlServerAnalyzer
{
    public static async Task<AnalyzeReport> RunAsync(IDbSession session, string? schema, CancellationToken ct)
    {
        var filter = schema is { Length: > 0 } ? $" AND s.name = '{schema.Replace("'", "''")}'" : "";
        var findings = new List<AnalyzeFinding>();

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT o.name, i.name
              FROM sys.indexes i
              JOIN sys.objects o ON o.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              LEFT JOIN sys.dm_db_index_usage_stats u
                     ON u.object_id = i.object_id AND u.index_id = i.index_id
                    AND u.database_id = DB_ID()
             WHERE o.type = 'U' AND i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique = 0
               AND ISNULL(u.user_seeks + u.user_scans + u.user_lookups, 0) = 0{filter}
             ORDER BY o.name, i.name
            """,
            r => new AnalyzeFinding("unused-index", "info",
                $"Unused index {r.GetString(1)}",
                $"{r.GetString(1)} on {r.GetString(0)} has no recorded reads since the last restart.",
                $"DROP INDEX [{r.GetString(1)}] ON [{r.GetString(0)}];"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, """
            SELECT TOP 20 OBJECT_NAME(d.object_id), ISNULL(d.equality_columns, d.inequality_columns),
                   CAST(s.avg_total_user_cost * s.avg_user_impact * (s.user_seeks + s.user_scans) AS bigint)
              FROM sys.dm_db_missing_index_details d
              JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
              JOIN sys.dm_db_missing_index_group_stats s ON s.group_handle = g.index_group_handle
             WHERE d.database_id = DB_ID()
             ORDER BY OBJECT_NAME(d.object_id)
            """,
            r => new AnalyzeFinding("missing-index", "warning",
                $"Missing index on {r.GetString(0)}",
                $"The engine itself reports a missing index on ({r.GetString(1)}), estimated impact {r.GetInt64(2)}.",
                $"CREATE INDEX [ix_{r.GetString(0)}] ON [{r.GetString(0)}] ({r.GetString(1)});"), ct));

        findings.AddRange(await AnalyzerSupport.QueryAsync(session, $"""
            SELECT o.name, i.name, CAST(p.avg_fragmentation_in_percent AS int)
              FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') p
              JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
              JOIN sys.objects o ON o.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE p.avg_fragmentation_in_percent > 30 AND p.page_count > 1000 AND i.name IS NOT NULL{filter}
             ORDER BY o.name, i.name
            """,
            r => new AnalyzeFinding("fragmentation", "warning",
                $"Fragmented index {r.GetString(1)}",
                $"{r.GetInt32(2)}% fragmentation on {r.GetString(0)}.",
                $"ALTER INDEX [{r.GetString(1)}] ON [{r.GetString(0)}] REBUILD;"), ct));

        return AnalyzerSupport.Sorted(findings);
    }
}

public static class SqliteAnalyzer
{
    /// SQLite exposes far less than the servers do. Only what it can answer honestly is reported.
    public static async Task<AnalyzeReport> RunAsync(IDbSession session, CancellationToken ct)
    {
        var findings = new List<AnalyzeFinding>();
        var tables = new List<string>();

        await using (var command = session.Connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = new List<(string Name, bool PrimaryKey)>();
            await using (var command = session.Connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) columns.Add((reader.GetString(1), reader.GetInt32(5) > 0));
            }

            if (!columns.Any(c => c.PrimaryKey))
                findings.Add(new AnalyzeFinding("no-primary-key", "warning",
                    $"{table} has no primary key",
                    "Rows in this table cannot be addressed for editing, and replication tools cannot track them.",
                    null));

            var indexColumns = new Dictionary<string, List<string>>();
            await using (var command = session.Connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list(\"{table.Replace("\"", "\"\"")}\")";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) indexColumns[reader.GetString(1)] = [];
            }

            foreach (var index in indexColumns.Keys.ToList())
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText = $"PRAGMA index_info(\"{index.Replace("\"", "\"\"")}\")";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    if (!reader.IsDBNull(2)) indexColumns[index].Add(reader.GetString(2));
            }

            foreach (var duplicate in indexColumns
                .GroupBy(i => string.Join(",", i.Value), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1 && g.Key.Length > 0))
                findings.Add(new AnalyzeFinding("duplicate-index", "warning",
                    $"Duplicate indexes on {table}",
                    $"These indexes cover ({duplicate.Key}): {string.Join(", ", duplicate.Select(d => d.Key))}.",
                    null));

            var indexed = indexColumns.Values.Where(c => c.Count > 0).Select(c => c[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await using (var command = session.Connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"")}\")";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var column = reader.GetString(3);
                    if (indexed.Contains(column)) continue;

                    findings.Add(new AnalyzeFinding("unindexed-foreign-key", "warning",
                        $"Unindexed foreign key on {table}",
                        $"{table}({column}) references {reader.GetString(2)} but has no index.",
                        $"CREATE INDEX \"ix_{table}_{column}\" ON \"{table}\" (\"{column}\");"));
                }
            }
        }

        var hasStatistics = false;
        await using (var command = session.Connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sqlite_stat1'";
            hasStatistics = Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
        }

        if (!hasStatistics && tables.Count > 0)
            findings.Add(new AnalyzeFinding("stale-statistics", "info",
                "ANALYZE has never run on this database",
                "Without sqlite_stat1 the query planner picks indexes by heuristics alone.",
                "ANALYZE;"));

        return AnalyzerSupport.Sorted(findings);
    }
}

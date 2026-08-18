using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

public sealed record HistoryEntry(long Id, string ConnectionId, string Sql,
    DateTimeOffset ExecutedAt, long? ElapsedMs, long? RowCount, string? Error);

/// Query history and open tabs. Both live server-side so a container restart does not lose them.
public sealed class WorkspaceStore
{
    private readonly string _connectionString;

    public WorkspaceStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                connection_id TEXT NOT NULL,
                sql TEXT NOT NULL,
                executed_at TEXT NOT NULL,
                elapsed_ms INTEGER NULL,
                row_count INTEGER NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_time ON history(id DESC);
            CREATE TABLE IF NOT EXISTS workspace (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public void AddHistory(string connectionId, string sql, long? elapsedMs, long? rowCount, string? error)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (connection_id, sql, executed_at, elapsed_ms, row_count, error)
            VALUES ($c, $s, $t, $e, $r, $err)
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$s", sql);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$e", (object?)elapsedMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)rowCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> ListHistory(string? connectionId, string? search, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, connection_id, sql, executed_at, elapsed_ms, row_count, error
              FROM history
             WHERE ($c IS NULL OR connection_id = $c)
               AND ($q IS NULL OR sql LIKE '%' || $q || '%')
             ORDER BY id DESC
             LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$c", (object?)connectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new HistoryEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    public void SaveTabs(string json) => SetValue("tabs", json);
    public string LoadTabs() => GetValue("tabs") ?? "[]";
    public void SaveLayout(string connectionId, string json) => SetValue($"layout:{connectionId}", json);
    public string? LoadLayout(string connectionId) => GetValue($"layout:{connectionId}");

    private void SetValue(string key, string value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspace (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private string? GetValue(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM workspace WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}

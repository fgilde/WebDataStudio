using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

public sealed record HistoryEntry(long Id, string ConnectionId, string Sql,
    DateTimeOffset ExecutedAt, long? ElapsedMs, long? RowCount, string? Error,
    /// Whether the result was kept with the entry. The rows themselves are not in the list — a
    /// history panel would carry every snapshot it ever took.
    bool HasSnapshot);

public sealed record SavedQuery(string Id, string Name, string? Folder, string Sql,
    string? ConnectionId, DateTimeOffset UpdatedAt);

/// Query history and open tabs. Both live server-side so a container restart does not lose them.
public sealed class WorkspaceStore
{
    private readonly string _connectionString;

    /// Why the store is unusable, or null when it is fine — see <see cref="SqliteFile"/> for why
    /// this is a state rather than an exception at startup.
    public string? Error { get; }

    public bool Available => Error is null;

    public string Path { get; }

    public WorkspaceStore(string dbPath)
    {
        Path = dbPath;

        var prepared = SqliteFile.Prepare(dbPath, """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                connection_id TEXT NOT NULL,
                sql TEXT NOT NULL,
                executed_at TEXT NOT NULL,
                elapsed_ms INTEGER NULL,
                row_count INTEGER NULL,
                error TEXT NULL,
                snapshot TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_time ON history(id DESC);
            CREATE TABLE IF NOT EXISTS workspace (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS saved_queries (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                folder TEXT NULL,
                sql TEXT NOT NULL,
                connection_id TEXT NULL,
                updated_at TEXT NOT NULL
            );
            """);

        _connectionString = prepared.ConnectionString;
        Error = prepared.Error;

        // A file created before snapshots existed has the table without that column, and SQLite has
        // no ADD COLUMN IF NOT EXISTS — so it is asked first.
        if (prepared.Available) AddColumnIfMissing("history", "snapshot", "TEXT NULL");
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        try
        {
            using var db = Open();

            using var check = db.CreateCommand();
            check.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = $n";
            check.Parameters.AddWithValue("$n", column);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            using var alter = db.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The column is what snapshots need, not what the studio needs. Everything else still
            // works without it, so a migration that cannot run is not a reason to refuse to start.
        }
    }

    private SqliteConnection Open()
    {
        if (!Available) throw new WorkspaceUnavailableException(Path, Error);

        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    /// <paramref name="snapshot"/> is the result as JSON, or null for the usual case of keeping
    /// only the statement.
    public void AddHistory(string connectionId, string sql, long? elapsedMs, long? rowCount,
        string? error, string? snapshot = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (connection_id, sql, executed_at, elapsed_ms, row_count, error, snapshot)
            VALUES ($c, $s, $t, $e, $r, $err, $snap)
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$s", sql);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$e", (object?)elapsedMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)rowCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$snap", (object?)snapshot ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> ListHistory(string? connectionId, string? search, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, connection_id, sql, executed_at, elapsed_ms, row_count, error,
                   snapshot IS NOT NULL
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetBoolean(7)));
        return result;
    }

    /// The kept result of one entry, or null when there is none.
    public string? LoadSnapshot(long id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        var value = cmd.ExecuteScalar();
        return value is string text ? text : null;
    }

    public void SaveTabs(string json) => SetValue("tabs", json);
    public string LoadTabs() => GetValue("tabs") ?? "[]";

    /// Any other workspace-scoped blob: snippets, layout presets, panel state. The key comes from
    /// the client, which is fine — this store belongs to the one user the container serves.
    public string? LoadItem(string key) => GetValue($"item:{key}");
    public void SaveItem(string key, string json) => SetValue($"item:{key}", json);
    public void SaveLayout(string connectionId, string json) => SetValue($"layout:{connectionId}", json);
    public string? LoadLayout(string connectionId) => GetValue($"layout:{connectionId}");

    // --- saved queries -------------------------------------------------------
    public IReadOnlyList<SavedQuery> ListSavedQueries()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        // Folder first, then name: the panel renders the list as a tree without re-sorting.
        cmd.CommandText = """
            SELECT id, name, folder, sql, connection_id, updated_at
              FROM saved_queries
             ORDER BY coalesce(folder, '') COLLATE NOCASE, name COLLATE NOCASE
            """;

        using var reader = cmd.ExecuteReader();
        var list = new List<SavedQuery>();

        while (reader.Read())
            list.Add(new SavedQuery(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));

        return list;
    }

    public SavedQuery SaveQuery(SavedQuery query)
    {
        var stored = query with
        {
            Id = string.IsNullOrEmpty(query.Id) ? Guid.NewGuid().ToString("n") : query.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
            // An empty folder string and no folder are the same thing; storing both would show
            // an empty group in the tree.
            Folder = string.IsNullOrWhiteSpace(query.Folder) ? null : query.Folder.Trim(),
        };

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_queries (id, name, folder, sql, connection_id, updated_at)
            VALUES ($id, $name, $folder, $sql, $conn, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, folder = excluded.folder, sql = excluded.sql,
                connection_id = excluded.connection_id, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", stored.Id);
        cmd.Parameters.AddWithValue("$name", stored.Name);
        cmd.Parameters.AddWithValue("$folder", (object?)stored.Folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sql", stored.Sql);
        cmd.Parameters.AddWithValue("$conn", (object?)stored.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", stored.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery();

        return stored;
    }

    public bool DeleteSavedQuery(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_queries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

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

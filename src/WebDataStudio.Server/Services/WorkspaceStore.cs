using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

public sealed record HistoryEntry(long Id, string ConnectionId, string Sql,
    DateTimeOffset ExecutedAt, long? ElapsedMs, long? RowCount, string? Error,
    /// Whether the result was kept with the entry. The rows themselves are not in the list — a
    /// history panel would carry every snapshot it ever took.
    bool HasSnapshot);

/// One line of the audit trail: who asked for what, and what came of it.
public sealed record AuditEntry(long Id, DateTimeOffset At, string User, string Role,
    string ConnectionId, string Action,
    /// What the handler wanted written down — the statement, the key, the table. Empty where the
    /// route says everything.
    string Detail,
    int Status, long ElapsedMs, string Address);

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
            -- How big every table was, whenever somebody looked. Two samples are a growth; one is
            -- just a size, which the structure panel already says.
            CREATE TABLE IF NOT EXISTS size_samples (
                connection_id TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                bytes INTEGER NOT NULL,
                rows INTEGER NULL,
                sampled_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_size_samples ON size_samples(connection_id, sampled_at);
            -- Who did what through the studio. Not a second query history: one row per request that
            -- changed something or took data out of the building.
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                user TEXT NOT NULL,
                role TEXT NOT NULL,
                connection_id TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL,
                status INTEGER NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                address TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_time ON audit(id DESC);
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

    public void AddAudit(AuditEntry entry)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit (at, user, role, connection_id, action, detail, status, elapsed_ms, address)
            VALUES ($at, $u, $r, $c, $a, $d, $s, $e, $ip)
            """;
        cmd.Parameters.AddWithValue("$at", entry.At.ToString("O"));
        cmd.Parameters.AddWithValue("$u", entry.User);
        cmd.Parameters.AddWithValue("$r", entry.Role);
        cmd.Parameters.AddWithValue("$c", entry.ConnectionId);
        cmd.Parameters.AddWithValue("$a", entry.Action);
        cmd.Parameters.AddWithValue("$d", entry.Detail);
        cmd.Parameters.AddWithValue("$s", entry.Status);
        cmd.Parameters.AddWithValue("$e", entry.ElapsedMs);
        cmd.Parameters.AddWithValue("$ip", entry.Address);
        cmd.ExecuteNonQuery();
    }

    /// The trail, newest first. `search` matches the action or the detail, which is how somebody
    /// looks for "who dropped that" without knowing what the route was called.
    public IReadOnlyList<AuditEntry> ListAudit(string? user, string? connectionId, string? search,
        int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, at, user, role, connection_id, action, detail, status, elapsed_ms, address
            FROM audit
            WHERE ($u IS NULL OR user = $u)
              AND ($c IS NULL OR connection_id = $c)
              AND ($q IS NULL OR action LIKE $q OR detail LIKE $q)
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$u", string.IsNullOrWhiteSpace(user) ? DBNull.Value : user);
        cmd.Parameters.AddWithValue("$c",
            string.IsNullOrWhiteSpace(connectionId) ? DBNull.Value : connectionId);
        cmd.Parameters.AddWithValue("$q",
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        var entries = new List<AuditEntry>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
            entries.Add(new AuditEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), reader.GetInt64(8), reader.GetString(9)));

        return entries;
    }

    /// Drops what is older than the retention. A trail nobody trimmed is a file that grows until
    /// somebody notices.
    public int TrimAudit(int days)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM audit WHERE at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        return cmd.ExecuteNonQuery();
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
    /// Records how big every table is right now. Called when somebody looks at the sizes and by the
    /// snapshot job, so the history builds itself rather than needing a decision.
    public void AddSizeSamples(string connectionId,
        IEnumerable<(string Schema, string Table, long Bytes, long? Rows)> sizes)
    {
        if (!Available) return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var at = DateTimeOffset.UtcNow.ToString("O");

        foreach (var (schema, table, bytes, rows) in sizes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO size_samples (connection_id, schema_name, table_name, bytes, rows, sampled_at)
                VALUES ($c, $s, $t, $b, $r, $a)
                """;
            command.Parameters.AddWithValue("$c", connectionId);
            command.Parameters.AddWithValue("$s", schema);
            command.Parameters.AddWithValue("$t", table);
            command.Parameters.AddWithValue("$b", bytes);
            command.Parameters.AddWithValue("$r", rows ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$a", at);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<(string Schema, string Table, long Bytes, long? Rows, DateTimeOffset At)>
        ListSizeSamples(string connectionId, DateTimeOffset since)
    {
        var samples = new List<(string, string, long, long?, DateTimeOffset)>();
        if (!Available) return samples;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_name, table_name, bytes, rows, sampled_at
              FROM size_samples
             WHERE connection_id = $c AND sampled_at >= $since
             ORDER BY sampled_at
            """;
        command.Parameters.AddWithValue("$c", connectionId);
        command.Parameters.AddWithValue("$since", since.ToString("O"));

        using var reader = command.ExecuteReader();
        while (reader.Read())
            samples.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4))));

        return samples;
    }

    /// Keeps the file from growing forever: a year of daily samples is a history, ten years of them
    /// is a habit nobody chose.
    public int TrimSizeSamples(TimeSpan keep)
    {
        if (!Available) return 0;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM size_samples WHERE sampled_at < $before";
        command.Parameters.AddWithValue("$before", DateTimeOffset.UtcNow.Subtract(keep).ToString("O"));

        return command.ExecuteNonQuery();
    }

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

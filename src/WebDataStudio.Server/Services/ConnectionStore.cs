using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// UI-managed connections, persisted in the application SQLite database with the
/// connection string encrypted at rest.
public sealed class ConnectionStore
{
    private readonly string _connectionString;
    private readonly SecretProtector _protector;
    private readonly Lock _gate = new();

    public ConnectionStore(string dbPath, SecretProtector protector)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _protector = protector;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS connections (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                engine TEXT NOT NULL,
                secret TEXT NOT NULL,
                read_only INTEGER NOT NULL DEFAULT 0,
                color TEXT NULL,
                grp TEXT NULL,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public IReadOnlyList<ConnectionSpec> List()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name, engine, secret, read_only, color, grp FROM connections ORDER BY name";
            using var reader = cmd.ExecuteReader();

            var result = new List<ConnectionSpec>();
            while (reader.Read()) result.Add(Read(reader));
            return result;
        }
    }

    public ConnectionSpec? Get(string id) =>
        List().FirstOrDefault(c => c.Id == id);

    public ConnectionSpec Add(ConnectionSpec spec)
    {
        var stored = spec with { Id = Guid.NewGuid().ToString("n"), Source = ConnectionSource.Stored };
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO connections (id, name, engine, secret, read_only, color, grp, created_at)
                VALUES ($id, $name, $engine, $secret, $ro, $color, $grp, $created)
                """;
            Bind(cmd, stored);
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) // UNIQUE constraint
            {
                throw new InvalidOperationException($"a connection named '{stored.Name}' already exists");
            }
        }
        return stored;
    }

    public void Update(ConnectionSpec spec)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE connections
                   SET name = $name, engine = $engine, secret = $secret,
                       read_only = $ro, color = $color, grp = $grp
                 WHERE id = $id
                """;
            Bind(cmd, spec);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM connections WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private void Bind(SqliteCommand cmd, ConnectionSpec spec)
    {
        cmd.Parameters.AddWithValue("$id", spec.Id);
        cmd.Parameters.AddWithValue("$name", spec.Name);
        cmd.Parameters.AddWithValue("$engine", spec.Engine);
        cmd.Parameters.AddWithValue("$secret", _protector.Protect(spec.ConnectionString));
        cmd.Parameters.AddWithValue("$ro", spec.ReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$color", (object?)spec.Color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$grp", (object?)spec.Group ?? DBNull.Value);
    }

    private ConnectionSpec Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        _protector.Unprotect(r.GetString(3)),
        r.GetInt32(4) == 1,
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        ConnectionSource.Stored);
}

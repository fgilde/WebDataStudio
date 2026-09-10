using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

/// Where an account came from: the deployment's own configuration, or this studio's store.
public enum UserSource
{
    /// `WDS_USERS`, or the single-account pair. Shown and never changed from the UI: a rollout owns
    /// it, which is also what makes it the way back in when the store has locked everybody out.
    Environment,

    /// Made in the admin panel and kept in the studio's own database.
    Stored,
}

/// The accounts an admin made, on disk beside the connections.
///
/// Accounts used to be deployment configuration and nothing else, which is right for a stack that
/// ships and wrong for a studio that runs for a year: somebody joins on a Tuesday. So they get a
/// store, with two rules that keep the old promise intact — a password is only ever written as a
/// PBKDF2 hash, and what the environment owns stays read-only here.
public sealed class StudioUserStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public StudioUserStore(string dbPath)
    {
        Path = dbPath;

        var prepared = SqliteFile.Prepare(dbPath, """
            CREATE TABLE IF NOT EXISTS studio_users (
                name TEXT PRIMARY KEY COLLATE NOCASE,
                role TEXT NOT NULL,
                secret TEXT NOT NULL,
                connections TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            """);

        _connectionString = prepared.ConnectionString;
        Error = prepared.Error;
    }

    public string Path { get; }

    /// Why the store is unusable, where it is. Reading degrades to "nothing stored" — a studio whose
    /// data directory is read-only still signs its environment accounts in.
    public string? Error { get; }

    public bool Available => Error is null;

    public IReadOnlyList<StudioUser> List()
    {
        if (!Available) return [];

        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name, role, secret, connections FROM studio_users ORDER BY name";

            var users = new List<StudioUser>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
                users.Add(new StudioUser(reader.GetString(0), reader.GetString(2),
                    UserRoles.Normalise(reader.GetString(1)), Names(reader.GetString(3)),
                    UserSource.Stored));

            return users;
        }
    }

    public StudioUser? Find(string name) =>
        List().FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// Keeps one account. The password arrives as somebody typed it and is written as a hash.
    public StudioUser Add(string name, string password, string role, IEnumerable<string> connections)
    {
        if (!Available) throw Unavailable();

        var user = new StudioUser(name.Trim(), UserStore.Hash(password), UserRoles.Normalise(role),
            new HashSet<string>(Clean(connections), StringComparer.OrdinalIgnoreCase),
            UserSource.Stored);

        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO studio_users (name, role, secret, connections, created_at)
                VALUES ($name, $role, $secret, $connections, $created)
                """;

            Bind(command, user);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) // UNIQUE constraint
            {
                throw new InvalidOperationException($"an account named '{user.Name}' already exists");
            }
        }

        return user;
    }

    /// Changes a role and the connections, and the password only when a new one was given: an admin
    /// fixing somebody's role should not need to know their password.
    public bool Update(string name, string role, IEnumerable<string> connections, string? password)
    {
        if (!Available) throw Unavailable();

        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();

            command.CommandText = password is { Length: > 0 }
                ? """
                  UPDATE studio_users
                     SET role = $role, connections = $connections, secret = $secret
                   WHERE name = $name
                  """
                : """
                  UPDATE studio_users
                     SET role = $role, connections = $connections
                   WHERE name = $name
                  """;

            command.Parameters.AddWithValue("$name", name.Trim());
            command.Parameters.AddWithValue("$role", UserRoles.Normalise(role));
            command.Parameters.AddWithValue("$connections", string.Join(',', Clean(connections)));

            if (password is { Length: > 0 })
                command.Parameters.AddWithValue("$secret", UserStore.Hash(password));

            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(string name)
    {
        if (!Available) throw Unavailable();

        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM studio_users WHERE name = $name";
            command.Parameters.AddWithValue("$name", name.Trim());

            return command.ExecuteNonQuery() > 0;
        }
    }

    private static void Bind(SqliteCommand command, StudioUser user)
    {
        command.Parameters.AddWithValue("$name", user.Name);
        command.Parameters.AddWithValue("$role", user.Role);
        command.Parameters.AddWithValue("$secret", user.Secret);
        command.Parameters.AddWithValue("$connections", string.Join(',', user.Connections));
    }

    private static IEnumerable<string> Clean(IEnumerable<string> connections) =>
        (connections ?? []).Select(c => c.Trim()).Where(c => c.Length > 0);

    private static HashSet<string> Names(string stored) =>
        new(stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private WorkspaceUnavailableException Unavailable() => new(Path, Error);
}

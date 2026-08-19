using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

/// Thrown when a request needs the application database and it is not usable. Mapped to a 503 in
/// Program.cs: the studio itself is running, this one dependency is not.
public sealed class WorkspaceUnavailableException(string path, string? error)
    : Exception($"the workspace database at '{path}' is not usable: {error}");

/// Opening the application database is the one piece of I/O that can take the whole studio down
/// with it: both stores are singletons, so a directory that never answers turns every request
/// that touches them into a hang and then a 500 — which is what an Azure Files share mounted at
/// /data does to SQLite. This prepares the file behind a deadline and reports what happened
/// instead of blocking a request thread forever.
public static class SqliteFile
{
    /// How long the studio waits for the file before it decides the directory is not usable.
    /// Generous for a local disk, short enough that a stuck share does not look like a crash.
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public sealed record Result(string ConnectionString, string? Error)
    {
        public bool Available => Error is null;
    }

    /// Creates the directory, opens the file and runs <paramref name="schema"/> against it. The
    /// work happens on a pool thread: a blocked SMB call cannot be cancelled, so the deadline
    /// abandons it rather than pretending it can be stopped.
    public static Result Prepare(string dbPath, string schema)
    {
        // DefaultTimeout is what SQLite waits on a lock; without it a share that holds one turns
        // into an indefinite wait inside every later query too.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = (int)Deadline.TotalSeconds,
        }.ToString();

        var work = Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

            using var db = new SqliteConnection(connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = schema;
            cmd.ExecuteNonQuery();
        });

        try
        {
            if (!work.Wait(Deadline))
                return new Result(connectionString,
                    $"'{dbPath}' did not answer within {Deadline.TotalSeconds:0} seconds. A network " +
                    "share (Azure Files, NFS, SMB) cannot carry a SQLite database reliably — point " +
                    "DB_PATH at local storage.");
        }
        catch (AggregateException e)
        {
            return new Result(connectionString, e.InnerException?.Message ?? e.Message);
        }
        catch (Exception e)
        {
            return new Result(connectionString, e.Message);
        }

        return new Result(connectionString, null);
    }
}

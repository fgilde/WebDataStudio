using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-workspace").FullName;
    private WorkspaceStore NewStore() => new(Path.Combine(_dir, "wds.db"));

    public void Dispose()
    {
        TestDirectory.Remove(_dir);
    }

    [Fact]
    public void Records_and_lists_history_newest_first()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT 1", 10, 1, null);
        store.AddHistory("c1", "SELECT 2", 20, 1, null);

        Assert.Equal("SELECT 2", store.ListHistory(null, null, 10)[0].Sql);
    }

    [Fact]
    public void Filters_history_by_connection()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT 1", 10, 1, null);
        store.AddHistory("c2", "SELECT 2", 10, 1, null);

        Assert.Single(store.ListHistory("c2", null, 10));
    }

    [Fact]
    public void Searches_history_text()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT * FROM people", 10, 3, null);
        store.AddHistory("c1", "SELECT * FROM orders", 10, 3, null);

        Assert.Single(store.ListHistory(null, "people", 10));
    }

    [Fact]
    public void Records_a_failed_query_with_its_error()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELCT 1", null, null, "syntax error");

        Assert.Equal("syntax error", store.ListHistory(null, null, 10)[0].Error);
    }

    [Fact]
    public void Honours_the_limit()
    {
        var store = NewStore();
        for (var i = 0; i < 10; i++) store.AddHistory("c1", $"SELECT {i}", 1, 1, null);

        Assert.Equal(3, store.ListHistory(null, null, 3).Count);
    }

    [Fact]
    public void Tabs_survive_a_reopen()
    {
        NewStore().SaveTabs("""[{"id":"t1","sql":"SELECT 1"}]""");
        Assert.Contains("SELECT 1", NewStore().LoadTabs());
    }

    [Fact]
    public void Tabs_default_to_an_empty_array()
    {
        Assert.Equal("[]", NewStore().LoadTabs());
    }

    [Fact]
    public void An_entry_without_a_snapshot_says_it_has_none()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT 1", 10, 1, null);

        var entry = store.ListHistory(null, null, 10)[0];
        Assert.False(entry.HasSnapshot);
        Assert.Null(store.LoadSnapshot(entry.Id));
    }

    [Fact]
    public void A_kept_result_comes_back_whole_and_is_not_in_the_list()
    {
        var store = NewStore();
        const string snapshot = """{"columns":["n"],"rows":[[1]],"truncated":false}""";
        store.AddHistory("c1", "SELECT 1 AS n", 10, 1, null, snapshot);

        var entry = store.ListHistory(null, null, 10)[0];
        Assert.True(entry.HasSnapshot);
        // The rows travel separately: a history panel would otherwise carry every snapshot it ever
        // took.
        Assert.Equal(snapshot, store.LoadSnapshot(entry.Id));
    }

    [Fact]
    public void A_file_written_before_snapshots_existed_gains_the_column()
    {
        var path = Path.Combine(_dir, "old.db");

        // What the store looked like before: the same table without the snapshot column.
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    connection_id TEXT NOT NULL,
                    sql TEXT NOT NULL,
                    executed_at TEXT NOT NULL,
                    elapsed_ms INTEGER NULL,
                    row_count INTEGER NULL,
                    error TEXT NULL
                );
                INSERT INTO history (connection_id, sql, executed_at)
                VALUES ('c1', 'SELECT 1', '2026-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var store = new WorkspaceStore(path);

        Assert.True(store.Available);
        var entry = Assert.Single(store.ListHistory(null, null, 10));
        Assert.False(entry.HasSnapshot);

        // And it keeps one from then on.
        store.AddHistory("c1", "SELECT 2", 1, 1, null, """{"columns":[],"rows":[]}""");
        Assert.True(store.ListHistory(null, null, 1)[0].HasSnapshot);
    }
}

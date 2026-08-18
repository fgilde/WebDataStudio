using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-workspace").FullName;
    private WorkspaceStore NewStore() => new(Path.Combine(_dir, "wds.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
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
}

using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-store").FullName;
    private ConnectionStore NewStore() =>
        new(Path.Combine(_dir, "wds.db"), new SecretProtector(_dir, null));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ConnectionSpec Sample(string name = "local-pg") =>
        new("", name, "postgresql", "Host=db;Password=hunter2", false, null, null, ConnectionSource.Stored);

    [Fact]
    public void Add_assigns_an_id_and_lists_the_connection()
    {
        var store = NewStore();
        var added = store.Add(Sample());

        Assert.NotEmpty(added.Id);
        Assert.Equal("local-pg", Assert.Single(store.List()).Name);
    }

    [Fact]
    public void Roundtrips_the_connection_string()
    {
        var store = NewStore();
        var id = store.Add(Sample()).Id;
        Assert.Equal("Host=db;Password=hunter2", store.Get(id)!.ConnectionString);
    }

    [Fact]
    public void Stores_the_connection_string_encrypted_on_disk()
    {
        var store = NewStore();
        store.Add(Sample());

        // The SQLite pool keeps the file open on Windows, so read it as a shared stream.
        using var stream = new FileStream(Path.Combine(_dir, "wds.db"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        Assert.DoesNotContain("hunter2", reader.ReadToEnd());
    }

    [Fact]
    public void Survives_a_reopen()
    {
        var id = NewStore().Add(Sample()).Id;
        Assert.Equal("local-pg", NewStore().Get(id)!.Name);
    }

    [Fact]
    public void Update_changes_the_fields()
    {
        var store = NewStore();
        var added = store.Add(Sample());
        store.Update(added with { Name = "renamed", ReadOnly = true });

        var reloaded = store.Get(added.Id)!;
        Assert.Equal("renamed", reloaded.Name);
        Assert.True(reloaded.ReadOnly);
    }

    [Fact]
    public void Delete_removes_the_connection()
    {
        var store = NewStore();
        var added = store.Add(Sample());
        store.Delete(added.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var store = NewStore();
        store.Add(Sample());
        Assert.Throws<InvalidOperationException>(() => store.Add(Sample()));
    }
}

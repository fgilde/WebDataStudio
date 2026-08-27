using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// The contract every object store keeps, run against each of them. A store that passes this is
/// interchangeable with the others as far as the tree, the preview and the query path are concerned —
/// which is the whole point of there being an interface.
///
/// Derive, hand it a store, and the six operations are checked. A provider that needs a container
/// starts one in its own fixture.
public abstract class ObjectStoreContract
{
    protected abstract IObjectStore Store { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// Puts the same small tree into the store: two objects in a prefix, one at the root.
    protected async Task SeedAsync()
    {
        await PutAsync("root.txt", "at the root");
        await PutAsync("exports/2026/a.csv", "n\n1\n");
        await PutAsync("exports/2026/b.csv", "n\n2\n");
    }

    protected async Task PutAsync(string key, string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await Store.WriteAsync(key, stream, "text/plain", Ct);
    }

    [Fact]
    public async Task A_listing_shows_the_prefixes_and_the_objects_directly_under_it()
    {
        await SeedAsync();

        var page = await Store.ListAsync("", null, 100, Ct);

        // "exports" is a prefix even where the store has no folders, and nothing from deeper down
        // leaks into this level.
        Assert.Contains(page.Entries, entry => entry.IsPrefix && entry.Name == "exports");
        Assert.Contains(page.Entries, entry => !entry.IsPrefix && entry.Name == "root.txt");
        Assert.DoesNotContain(page.Entries, entry => entry.Name == "a.csv");
    }

    [Fact]
    public async Task A_listing_goes_one_level_deeper_when_asked()
    {
        await SeedAsync();

        var page = await Store.ListAsync("exports/2026", null, 100, Ct);

        Assert.Equal(2, page.Entries.Count(entry => !entry.IsPrefix));
        Assert.All(page.Entries, entry => Assert.True(entry.SizeBytes > 0, entry.Name));
    }

    [Fact]
    public async Task A_listing_is_paged_rather_than_walked()
    {
        await SeedAsync();

        var first = await Store.ListAsync("exports/2026", null, 1, Ct);

        Assert.Single(first.Entries);
        Assert.NotNull(first.Cursor);

        var second = await Store.ListAsync("exports/2026", first.Cursor, 1, Ct);

        Assert.Single(second.Entries);
        Assert.NotEqual(first.Entries[0].Key, second.Entries[0].Key);
    }

    [Fact]
    public async Task An_object_reports_its_size_and_type_without_being_read()
    {
        await SeedAsync();

        var found = await Store.HeadAsync("exports/2026/a.csv", Ct);

        Assert.NotNull(found);
        Assert.Equal(4, found!.SizeBytes);
        Assert.NotNull(found.Modified);
    }

    [Fact]
    public async Task An_object_that_is_not_there_is_null_rather_than_an_error() =>
        Assert.Null(await Store.HeadAsync("exports/2026/nope.csv", Ct));

    [Fact]
    public async Task An_object_reads_back_what_was_written()
    {
        await SeedAsync();

        await using var stream = await Store.OpenReadAsync("root.txt", Ct);
        using var reader = new StreamReader(stream);

        Assert.Equal("at the root", await reader.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task A_deleted_object_is_gone()
    {
        await SeedAsync();
        await Store.DeleteAsync("root.txt", Ct);

        Assert.Null(await Store.HeadAsync("root.txt", Ct));
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error() =>
        await Store.DeleteAsync("never-existed.txt", Ct);

    [Fact]
    public async Task Every_object_has_a_uri_duckdb_can_read()
    {
        await SeedAsync();

        var uri = Store.SqlUri("exports/2026/a.csv");

        Assert.EndsWith("exports/2026/a.csv", uri);
        // Backslashes would end a string literal in the SQL this goes into.
        Assert.DoesNotContain('\\', uri);
    }
}

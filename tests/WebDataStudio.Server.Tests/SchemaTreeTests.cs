using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The tree stops at the object in every driver; the endpoint grows the last level from the
/// object's own detail. Without it a table expands into nothing.
public class SchemaTreeTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-tree").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            CREATE INDEX ix_people_city ON people(city);
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                person_id INTEGER NOT NULL REFERENCES people(id),
                total REAL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONNECTIONS"] = JsonSerializer.Serialize(new[]
                {
                    new { name = "SHOP", engine = "sqlite", connectionString = $"Data Source={_db}" },
                }),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<List<(string Kind, string Label, string? Detail, string Ref)>> ChildrenAsync(
        HttpClient client, string id, string parent)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}?parent={Uri.EscapeDataString(parent)}", TestContext.Current.CancellationToken);

        return body.EnumerateArray()
            .Select(e => (
                e.GetProperty("kind").GetString()!,
                e.GetProperty("label").GetString()!,
                e.GetProperty("detail").GetString(),
                e.GetProperty("ref").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task A_table_expands_into_its_columns_indexes_and_keys()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var children = await ChildrenAsync(client, id, "Table:main/orders");

        Assert.Contains(children, c => c.Kind == "Column" && c.Label == "person_id");
        Assert.Contains(children, c => c.Kind == "ForeignKey");
        // Columns come first: that is the order somebody reading a table expects.
        Assert.Equal("Column", children[0].Kind);
    }

    [Fact]
    public async Task A_column_carries_its_type_and_key_in_the_detail()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var children = await ChildrenAsync(client, id, "Table:main/people");

        var key = children.Single(c => c.Kind == "Column" && c.Label == "id");
        var name = children.Single(c => c.Kind == "Column" && c.Label == "name");

        Assert.Contains("pk", key.Detail!);
        Assert.Contains("not null", name.Detail!);
    }

    [Fact]
    public async Task An_index_shows_up_with_the_columns_it_covers()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var children = await ChildrenAsync(client, id, "Table:main/people");
        var index = children.Single(c => c.Kind == "Index" && c.Label == "ix_people_city");

        Assert.Contains("city", index.Detail!);
    }

    [Fact]
    public async Task Every_child_reference_keeps_the_path_of_its_table()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        // An action on a column has to be able to work out which table it belongs to.
        foreach (var child in await ChildrenAsync(client, id, "Table:main/people"))
            Assert.StartsWith($"{child.Kind}:main/people/", child.Ref);
    }

    [Fact]
    public async Task A_folder_still_lists_what_the_driver_returns()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var tables = await ChildrenAsync(client, id, "TableFolder:main/tables");

        Assert.Contains(tables, t => t.Kind == "Table" && t.Label == "people");
        Assert.Contains(tables, t => t.Kind == "Table" && t.Label == "orders");
    }
}

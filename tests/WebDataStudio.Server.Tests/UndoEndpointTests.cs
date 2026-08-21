using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// Undo end to end: the change is applied, the inverse is offered as a script, applying it puts the
/// data back, and the entry is gone afterwards.
public class UndoEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-undo").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "people.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            INSERT INTO people VALUES (1, 'Ada', 'London'), (2, 'Grace', 'New York');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PEOPLE"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private const string Ref = "Table:main/people";

    /// Previews the changes and applies them, returning the apply response.
    private static async Task<JsonElement> ChangeAsync(HttpClient client, string id, object changes)
    {
        var ct = TestContext.Current.CancellationToken;
        var preview = await client.PostAsJsonAsync($"/api/data/{id}/preview-changes?ref={Ref}", changes, ct);
        preview.EnsureSuccessStatusCode();
        var hash = (await preview.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("hash").GetString();

        var applied = await client.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref={Ref}",
            new { hash }, ct);
        applied.EnsureSuccessStatusCode();
        return await applied.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<List<string?>> NamesAsync(HttpClient client, string id)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref={Ref}&sort=id", TestContext.Current.CancellationToken);

        return [.. body.GetProperty("rows").EnumerateArray().Select(r => r[1].GetString())];
    }

    [Fact]
    public async Task An_update_can_be_taken_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var applied = await ChangeAsync(client, id, new
        {
            changes = new[]
            {
                new { kind = "update", key = new { id = 1 }, values = new { name = "Ada L." } },
                new { kind = "update", key = new { id = 2 }, values = new { name = "Grace H." } },
            },
        });

        Assert.True(applied.GetProperty("undoable").GetBoolean());
        Assert.Equal(["Ada L.", "Grace H."], await NamesAsync(client, id));

        // The inverse is shown as a script before anything runs, like every other change.
        var preview = await client.PostAsync($"/api/data/{id}/undo/preview?ref={Ref}", null, ct);
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("2 updates", body.GetProperty("label").GetString());
        Assert.Contains("UPDATE", body.GetProperty("script").GetString());

        var undone = await client.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref={Ref}",
            new { hash = body.GetProperty("hash").GetString() }, ct);
        undone.EnsureSuccessStatusCode();

        Assert.Equal(["Ada", "Grace"], await NamesAsync(client, id));
    }

    [Fact]
    public async Task A_delete_comes_back_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await ChangeAsync(client, id, new
        {
            changes = new[] { new { kind = "delete", key = new { id = 1 }, values = new { } } },
        });

        Assert.Equal(["Grace"], await NamesAsync(client, id));

        var preview = await client.PostAsync($"/api/data/{id}/undo/preview?ref={Ref}", null, ct);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>(ct);
        await client.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref={Ref}",
            new { hash = body.GetProperty("hash").GetString() }, ct);

        // Every column, not only the ones the grid happened to be showing.
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}?ref={Ref}&sort=id", ct);
        var first = rows.GetProperty("rows").EnumerateArray().First();

        Assert.Equal("Ada", first[1].GetString());
        Assert.Equal("London", first[2].GetString());
    }

    [Fact]
    public async Task Undoing_twice_finds_nothing_left()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await ChangeAsync(client, id, new
        {
            changes = new[] { new { kind = "update", key = new { id = 1 }, values = new { city = "Paris" } } },
        });

        var preview = await client.PostAsync($"/api/data/{id}/undo/preview?ref={Ref}", null, ct);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>(ct);
        await client.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref={Ref}",
            new { hash = body.GetProperty("hash").GetString() }, ct);

        // The entry is consumed by the apply, not by the preview: a preview somebody cancelled
        // must not lose them their undo.
        var again = await client.PostAsync($"/api/data/{id}/undo/preview?ref={Ref}", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}/undo?ref={Ref}", ct);
        Assert.False(state.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Nothing_to_undo_is_a_plain_answer_not_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}/undo?ref={Ref}", ct);

        Assert.False(state.GetProperty("available").GetBoolean());
    }
}

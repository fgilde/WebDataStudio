using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// Notes on objects: what people know, kept next to the object rather than in a chat message.
public class ObjectNoteTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-notes").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory(params (string Key, string? Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DB_PATH"] = Path.Combine(_dir, "wds.db") }
                    .Concat(extra.Select(pair =>
                        new KeyValuePair<string, string?>(pair.Key, pair.Value)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value))));

    private static async Task<JsonElement> AddAsync(HttpClient client, string conn, string reference,
        string body)
    {
        var response = await client.PostAsJsonAsync($"/api/notes/{conn}",
            new { @ref = reference, body }, Ct);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task A_note_carries_a_name_and_a_date()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var note = await AddAsync(client, "pg", "Table:public/orders",
            "  The status column is a string because the enum came later.  ");

        // Trimmed, because a note that starts with a newline reads like a mistake.
        Assert.Equal("The status column is a string because the enum came later.",
            note.GetProperty("body").GetString());
        // Nobody signed in is one person at a machine, and saying so beats an empty column.
        Assert.Equal("anonymous", note.GetProperty("author").GetString());
        Assert.True(note.GetProperty("id").GetInt64() > 0);
    }

    [Fact]
    public async Task A_signed_in_person_is_named()
    {
        using var factory = Factory(("WDS_USER", "ada"), ("WDS_PASSWORD", "secret-secret"));
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/auth/login",
            new { username = "ada", password = "secret-secret" }, Ct)).EnsureSuccessStatusCode();

        var note = await AddAsync(client, "pg", "Table:public/orders", "Mine.");

        Assert.Equal("ada", note.GetProperty("author").GetString());
    }

    [Fact]
    public async Task The_notes_of_one_object_come_back_newest_first()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        await AddAsync(client, "pg", "Table:public/orders", "First.");
        await AddAsync(client, "pg", "Table:public/orders", "Second.");
        await AddAsync(client, "pg", "Table:public/customers", "Another object.");

        var notes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/notes/pg?ref={Uri.EscapeDataString("Table:public/orders")}", Ct);

        var bodies = notes.EnumerateArray().Select(n => n.GetProperty("body").GetString()).ToList();

        Assert.Equal(["Second.", "First."], bodies);
    }

    [Fact]
    public async Task And_a_search_finds_what_somebody_wrote_once()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        await AddAsync(client, "pg", "Table:public/orders",
            "The status column is a string because the enum came later.");
        await AddAsync(client, "pg", "Table:public/customers", "Nothing to see here.");

        var found = await client.GetFromJsonAsync<JsonElement>("/api/notes?search=enum", Ct);

        var note = Assert.Single(found.EnumerateArray().ToList());
        Assert.Contains("enum", note.GetProperty("body").GetString());

        // An empty search is not "everything": that would be a page nobody asked for.
        var nothing = await client.GetFromJsonAsync<JsonElement>("/api/notes", Ct);
        Assert.Empty(nothing.EnumerateArray());
    }

    [Fact]
    public async Task An_empty_note_is_not_a_note()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/notes/pg",
                new { @ref = "Table:public/orders", body = "   " }, Ct)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/notes/pg",
                new { @ref = "", body = "Something." }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_note_can_be_taken_back()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var note = await AddAsync(client, "pg", "Table:public/orders", "Wrong.");
        var id = note.GetProperty("id").GetInt64();

        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/notes/pg/{id}", Ct)).StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(
            $"/api/notes/pg?ref={Uri.EscapeDataString("Table:public/orders")}", Ct)).EnumerateArray());

        // And again is a 404 rather than a second success.
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/notes/pg/{id}", Ct)).StatusCode);
    }
}

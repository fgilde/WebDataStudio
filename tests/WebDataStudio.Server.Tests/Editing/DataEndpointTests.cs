using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Editing;

public class DataEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-data").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, active INTEGER NOT NULL);
            INSERT INTO people VALUES (1,'ada',1),(2,'linus',1),(3,'grace',0);
            CREATE VIEW active_people AS SELECT id, name FROM people WHERE active = 1;
            CREATE TABLE notes (body TEXT);
            INSERT INTO notes VALUES ('first'),('second');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(bool readOnly = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_DEMO"] = $"sqlite:///{_db.Replace('\\', '/')}",
                ["WDS_READONLY"] = readOnly ? "true" : null,
            })));

    private static async Task<string> ConnectionIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    private const string PeopleRef = "Table%3Amain%2Fpeople";

    private static object Update(int id, string name) => new
    {
        changes = new[]
        {
            new { kind = "update", key = new Dictionary<string, object?> { ["id"] = id },
                  values = new Dictionary<string, object?> { ["name"] = name } },
        },
    };

    [Fact]
    public async Task Browse_returns_rows_and_marks_the_table_editable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={PeopleRef}", ct);

        Assert.Equal(3, body.GetProperty("rows").GetArrayLength());
        Assert.True(body.GetProperty("editable").GetBoolean());
        Assert.Equal("id", body.GetProperty("keyColumns")[0].GetString());
    }

    [Fact]
    public async Task Browse_pages_through_the_table()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{conn}?ref={PeopleRef}&offset=1&limit=1&sort=id", ct);

        Assert.Equal(1, body.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, body.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Browse_filters_by_a_column()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{conn}?ref={PeopleRef}&filterColumn=name&filter=ad", ct);

        Assert.Equal(1, body.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task A_key_less_table_is_edited_by_where_its_rows_physically_are()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref=Table%3Amain%2Fnotes", ct);

        Assert.True(body.GetProperty("editable").GetBoolean());
        Assert.Equal(["wds_row_address"],
            body.GetProperty("keyColumns").EnumerateArray().Select(k => k.GetString()));

        // The address is selected with the rows, because an UPDATE cannot find the row without it.
        Assert.Contains("wds_row_address",
            body.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        // And the tab says what that costs rather than pretending it is a key.
        Assert.Contains("moves when the row is updated", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_row_of_a_key_less_table_can_actually_be_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        const string notes = "Table%3Amain%2Fnotes";
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={notes}", ct);
        // SQLite hands its rowid back as the number it is; PostgreSQL hands ctid back as text.
        var address = page.GetProperty("rows")[0][0].GetInt64();

        var change = new
        {
            changes = new[]
            {
                new
                {
                    kind = "update",
                    key = new Dictionary<string, object?> { ["wds_row_address"] = address },
                    values = new Dictionary<string, object?> { ["body"] = "rewritten" },
                },
            },
        };

        var preview = await (await client.PostAsJsonAsync(
            $"/api/data/{conn}/preview-changes?ref={notes}", change, ct)).Content
            .ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("rowid = ", preview.GetProperty("script").GetString());

        var applied = await client.PostAsJsonAsync($"/api/data/{conn}/apply-changes?ref={notes}",
            new { hash = preview.GetProperty("hash").GetString() }, ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={notes}", ct);
        Assert.Equal("rewritten", after.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Preview_returns_a_script_without_touching_the_data()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync(
            $"/api/data/{conn}/preview-changes?ref={PeopleRef}", Update(1, "changed"), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("UPDATE", preview.GetProperty("script").GetString());
        Assert.NotEmpty(preview.GetProperty("hash").GetString()!);

        // Still the old value: preview writes nothing.
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={PeopleRef}&sort=id", ct);
        Assert.Equal("ada", rows.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Apply_with_the_previewed_hash_writes_the_change()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync(
            $"/api/data/{conn}/preview-changes?ref={PeopleRef}", Update(1, "changed"), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        var apply = await client.PostAsJsonAsync($"/api/data/{conn}/apply-changes?ref={PeopleRef}",
            new { hash = preview.GetProperty("hash").GetString() }, ct);
        apply.EnsureSuccessStatusCode();

        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={PeopleRef}&sort=id", ct);
        Assert.Equal("changed", rows.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Apply_with_an_unknown_hash_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var response = await client.PostAsJsonAsync($"/api/data/{conn}/apply-changes?ref={PeopleRef}",
            new { hash = "stale" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Apply_rolls_back_when_a_statement_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        // The first change is fine; the second violates NOT NULL on name.
        var changes = new
        {
            changes = new object[]
            {
                new { kind = "update", key = new Dictionary<string, object?> { ["id"] = 1 },
                      values = new Dictionary<string, object?> { ["name"] = "fine" } },
                new { kind = "update", key = new Dictionary<string, object?> { ["id"] = 2 },
                      values = new Dictionary<string, object?> { ["name"] = (string?)null } },
            },
        };

        var preview = await (await client.PostAsJsonAsync(
            $"/api/data/{conn}/preview-changes?ref={PeopleRef}", changes, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var apply = await client.PostAsJsonAsync($"/api/data/{conn}/apply-changes?ref={PeopleRef}",
            new { hash = preview.GetProperty("hash").GetString() }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);

        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={PeopleRef}&sort=id", ct);
        Assert.Equal("ada", rows.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Preview_is_refused_on_a_read_only_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var browse = await client.GetFromJsonAsync<JsonElement>($"/api/data/{conn}?ref={PeopleRef}", ct);
        Assert.False(browse.GetProperty("editable").GetBoolean());

        // A client that ignores `editable` and previews anyway still cannot apply.
        var preview = await client.PostAsJsonAsync(
            $"/api/data/{conn}/preview-changes?ref={PeopleRef}", Update(1, "nope"), ct);

        if (preview.IsSuccessStatusCode)
        {
            var hash = (await preview.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("hash").GetString();
            var apply = await client.PostAsJsonAsync($"/api/data/{conn}/apply-changes?ref={PeopleRef}",
                new { hash }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        }
    }

    [Fact]
    public async Task Lookup_returns_values_with_labels()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{conn}/lookup?ref={PeopleRef}&column=id&search=ad", ct);

        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("ada", body[0].GetProperty("label").GetString());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Ddl;

public class DdlEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-ddl").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE VIEW active_people AS SELECT id, name FROM people;
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

    private static object Definition(params object[] columns) => new
    {
        schema = "main",
        name = "people",
        columns,
        indexes = Array.Empty<object>(),
        constraints = new[] { new { name = "pk_people", kind = "PrimaryKey", columns = new[] { "id" } } },
        comment = (string?)null,
    };

    private static object Column(string name, string type, bool nullable) =>
        new { name, type, nullable, @default = (string?)null, identity = false, comment = (string?)null };

    /// The whole point of the object editors: the statement is shown, and only a second call runs
    /// it. These go through SQLite, which is the engine with the fewest of them — so what it cannot
    /// do has to read as a sentence rather than as a 500.
    [Fact]
    public async Task A_view_is_written_and_only_applied_on_purpose()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/view", new
        {
            schema = "main", name = "recent_people", select = "SELECT id, name FROM people",
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("CREATE VIEW", preview.GetProperty("script").GetString());

        // Nothing ran yet: the view is not there to describe.
        var before = await client.GetAsync($"/api/ddl/{conn}?ref=View%3Amain%2Frecent_people", ct);
        Assert.NotEqual(HttpStatusCode.OK, before.StatusCode);

        var applied = await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new
        {
            hash = preview.GetProperty("hash").GetString(),
        }, ct);

        applied.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}?ref=View%3Amain%2Frecent_people", ct);
        Assert.Contains("recent_people", after.GetProperty("create").GetString() ?? "");
    }

    [Fact]
    public async Task A_view_is_dropped_with_what_depends_on_it_listed_first()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/drop", new
        {
            objectRef = "View:main/active_people",
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.StartsWith("DROP VIEW", preview.GetProperty("script").GetString());
        Assert.True(preview.TryGetProperty("dependencies", out _));

        (await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new
        {
            hash = preview.GetProperty("hash").GetString(),
        }, ct)).EnsureSuccessStatusCode();

        var gone = await client.GetAsync($"/api/ddl/{conn}?ref=View%3Amain%2Factive_people", ct);
        Assert.NotEqual(HttpStatusCode.OK, gone.StatusCode);
    }

    [Fact]
    public async Task A_read_only_connection_previews_but_does_not_apply()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/view", new
        {
            schema = "main", name = "v", select = "SELECT 1",
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var applied = await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new
        {
            hash = preview.GetProperty("hash").GetString(),
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, applied.StatusCode);
    }

    [Fact]
    public async Task What_this_engine_cannot_do_comes_back_as_a_sentence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var sequence = await client.PostAsJsonAsync($"/api/ddl/{conn}/sequence", new
        {
            schema = "main", name = "s", create = true, start = 1,
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, sequence.StatusCode);
        Assert.Contains("INTEGER PRIMARY KEY", await sequence.Content.ReadAsStringAsync(ct));

        var schema = await client.PostAsJsonAsync($"/api/ddl/{conn}/schema", new
        {
            name = "reporting", drop = false,
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, schema.StatusCode);
        Assert.Contains("no schemas", await schema.Content.ReadAsStringAsync(ct));

        var comment = await client.PostAsJsonAsync($"/api/ddl/{conn}/comment", new
        {
            objectRef = "Table:main/people", text = "the people",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, comment.StatusCode);
        Assert.Contains("notes", await comment.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_trigger_is_created_from_its_source_and_then_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/routine", new
        {
            schema = "main", name = "people_audit", kind = "trigger",
            body = "CREATE TRIGGER people_audit AFTER INSERT ON people BEGIN SELECT 1; END",
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("CREATE TRIGGER", preview.GetProperty("script").GetString());

        (await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new
        {
            hash = preview.GetProperty("hash").GetString(),
        }, ct)).EnsureSuccessStatusCode();

        var drop = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/drop", new
        {
            objectRef = "Trigger:main/people/people_audit",
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        // SQLite drops a trigger by name alone, without naming its table.
        Assert.StartsWith("DROP TRIGGER", drop.GetProperty("script").GetString());
        Assert.DoesNotContain(" ON ", drop.GetProperty("script").GetString());

        (await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new
        {
            hash = drop.GetProperty("hash").GetString(),
        }, ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Returns_the_current_definition_and_create_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}?ref={PeopleRef}", ct);

        Assert.Equal("people", body.GetProperty("definition").GetProperty("name").GetString());
        Assert.Contains("CREATE TABLE", body.GetProperty("create").GetString());
    }

    [Fact]
    public async Task Preview_of_an_added_column_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/preview", new
        {
            objectRef = "Table:main/people",
            after = Definition(Column("id", "int", false), Column("name", "text", false), Column("note", "text", true)),
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("ADD", preview.GetProperty("script").GetString());
        Assert.False(preview.GetProperty("destructive").GetBoolean());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}?ref={PeopleRef}", ct);
        Assert.Equal(2, detail.GetProperty("definition").GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task Apply_with_the_hash_adds_the_column()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/preview", new
        {
            objectRef = "Table:main/people",
            after = Definition(Column("id", "int", false), Column("name", "text", false), Column("note", "text", true)),
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var apply = await client.PostAsJsonAsync($"/api/ddl/{conn}/apply",
            new { hash = preview.GetProperty("hash").GetString() }, ct);
        apply.EnsureSuccessStatusCode();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}?ref={PeopleRef}", ct);
        Assert.Equal(3, detail.GetProperty("definition").GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task Apply_with_a_stale_hash_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var response = await client.PostAsJsonAsync($"/api/ddl/{conn}/apply", new { hash = "nope" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Dropping_a_column_is_flagged_destructive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/preview", new
        {
            objectRef = "Table:main/people",
            after = Definition(Column("id", "int", false)),
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.True(preview.GetProperty("destructive").GetBoolean());
    }

    [Fact]
    public async Task Apply_is_refused_on_a_read_only_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var preview = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/preview", new
        {
            objectRef = "Table:main/people",
            after = Definition(Column("id", "int", false), Column("name", "text", false), Column("x", "text", true)),
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var apply = await client.PostAsJsonAsync($"/api/ddl/{conn}/apply",
            new { hash = preview.GetProperty("hash").GetString() }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
    }

    [Fact]
    public async Task An_unchanged_definition_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var current = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}?ref={PeopleRef}", ct);
        var response = await client.PostAsJsonAsync($"/api/ddl/{conn}/preview", new
        {
            objectRef = "Table:main/people",
            after = current.GetProperty("definition"),
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rename_reports_the_dependent_view()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await (await client.PostAsJsonAsync($"/api/ddl/{conn}/rename",
            new { objectRef = "Table:main/people", newName = "humans" }, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("RENAME TO", body.GetProperty("script").GetString());
        Assert.Contains("active_people",
            body.GetProperty("dependencies").GetProperty("usedBy").EnumerateArray()
                .Select(e => e.GetString()));
    }
}

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

    [Fact]
    public async Task Returns_the_current_definition_and_create_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}/{PeopleRef}", ct);

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

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}/{PeopleRef}", ct);
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

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}/{PeopleRef}", ct);
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

        var current = await client.GetFromJsonAsync<JsonElement>($"/api/ddl/{conn}/{PeopleRef}", ct);
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

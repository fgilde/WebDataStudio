using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class ArchiveNameTests
{
    [Fact]
    public void A_name_is_a_file_name_and_nothing_else()
    {
        // Anything that could climb out of the directory is not part of a name.
        Assert.Equal("etcpasswd", Archives.Sanitize("../../etc/passwd"));
        Assert.Equal("before-the-migration", Archives.Sanitize("before the migration"));
        Assert.Equal("orders.2026", Archives.Sanitize("orders.2026"));
        Assert.Equal("weird", Archives.Sanitize("  <weird>  "));
    }

    [Fact]
    public void A_name_that_is_nothing_at_all_is_refused() =>
        Assert.Throws<ArgumentException>(() => Archives.Sanitize("///"));

    [Fact]
    public void A_very_long_name_is_cut_rather_than_written()
    {
        Assert.Equal(80, Archives.Sanitize(new string('a', 500)).Length);
    }
}

public class ArchiveScriptTests
{
    private static readonly ArchiveColumn[] Columns =
        [new("id", "integer"), new("name", "text"), new("note", "text")];

    private static object?[] Row(params string[] json) =>
        [.. json.Select(text => (object?)JsonDocument.Parse(text).RootElement)];

    [Fact]
    public void Rows_become_inserts_with_the_engine_s_own_quoting()
    {
        var script = ArchiveScript.Inserts("postgresql", "public.people", Columns,
            [Row("1", "\"ada\"", "null")]);

        Assert.Equal("""INSERT INTO public.people ("id", "name", "note") VALUES (1, 'ada', NULL);""",
            script);

        Assert.Contains("`id`", ArchiveScript.Inserts("mysql", "people", Columns, [Row("1", "\"a\"", "null")]));
        Assert.Contains("[id]", ArchiveScript.Inserts("sqlserver", "people", Columns, [Row("1", "\"a\"", "null")]));
    }

    [Fact]
    public void A_quote_in_a_value_is_doubled_rather_than_ending_the_string()
    {
        var script = ArchiveScript.Inserts("postgresql", "t", [new("v", "text")],
            [Row("\"o'brien\"")]);

        Assert.Contains("'o''brien'", script);
    }

    [Fact]
    public void An_object_goes_back_as_its_own_text()
    {
        var script = ArchiveScript.Inserts("postgresql", "t", [new("v", "jsonb")],
            [Row("""{"a":1}""")]);

        Assert.Contains("""'{"a":1}'""", script);
    }

    [Fact]
    public void An_empty_archive_says_so_instead_of_producing_nothing()
    {
        var script = ArchiveScript.Inserts("postgresql", "t", [new("v", "text")], []);

        Assert.StartsWith("--", script);
    }

    [Fact]
    public void An_archive_with_no_columns_is_not_a_script() =>
        Assert.Throws<ArgumentException>(() => ArchiveScript.Inserts("postgresql", "t", [], []));
}

/// Archives through the endpoints: keeping a result, reading it back, and what happens to a masked
/// column on the way in.
public class ArchiveEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-archive").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_token TEXT);
            INSERT INTO people VALUES (1,'ada','tok-1'),(2,'linus','tok-2');
            """;
        await cmd.ExecuteNonQueryAsync();
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
                ["WDS_ARCHIVE_DIR"] = Path.Combine(_dir, "archives"),
                ["WDS_CONN_DEMO"] = $"sqlite:///{_db.Replace('\\', '/')}",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_result_is_kept_and_read_back()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var saved = await client.PostAsJsonAsync("/api/archives/people-now",
            new { connectionId = conn, sql = "SELECT id, name FROM people ORDER BY id" }, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var info = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, info.RootElement.GetProperty("rows").GetInt32());

        using var page = JsonDocument.Parse(
            await client.GetStringAsync("/api/archives/people-now", Ct));

        Assert.Equal(2, page.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("ada", page.RootElement.GetProperty("rows")[0][1].GetString());
        Assert.Equal(["id", "name"],
            page.RootElement.GetProperty("columns").EnumerateArray()
                .Select(column => column.GetProperty("name").GetString()).ToList());
    }

    [Fact]
    public async Task A_whole_table_can_be_kept_without_writing_the_statement()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var saved = await client.PostAsJsonAsync("/api/archives/whole-table",
            new { connectionId = conn, objectRef = "Table:main/people" }, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/archives/whole-table", Ct));
        Assert.Equal(2, page.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task A_masked_column_stays_masked_in_the_file()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/with-secrets",
            new { connectionId = conn, sql = "SELECT id, api_token FROM people" }, Ct);

        // An archive of a masked column would be a way around the masking, so it is masked on the
        // way in — like an export. The file itself is checked, not only the reply: the point is that
        // the secret is not on disk.
        var text = await File.ReadAllTextAsync(
            Path.Combine(_dir, "archives", "with-secrets.ndjson"), Ct);

        Assert.DoesNotContain("tok-1", text);

        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/archives/with-secrets", Ct));
        Assert.Equal(SensitiveColumns.Mask, page.RootElement.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task The_listing_names_what_is_there_and_where_it_lives()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/one",
            new { connectionId = conn, sql = "SELECT 1 AS n" }, Ct);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/archives", Ct));

        Assert.True(list.RootElement.GetProperty("available").GetBoolean());
        Assert.Contains("archives", list.RootElement.GetProperty("path").GetString());

        var item = list.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("one", item.GetProperty("name").GetString());
        Assert.Contains("SELECT 1", item.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Keeping_the_same_name_twice_replaces_it()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/twice",
            new { connectionId = conn, sql = "SELECT id FROM people" }, Ct);
        await client.PostAsJsonAsync("/api/archives/twice",
            new { connectionId = conn, sql = "SELECT id FROM people WHERE id = 1" }, Ct);

        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/archives/twice", Ct));
        Assert.Equal(1, page.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task The_rows_come_back_as_inserts_for_wherever_they_should_go_next()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/restore-me",
            new { connectionId = conn, sql = "SELECT id, name FROM people ORDER BY id" }, Ct);

        var response = await client.PostAsync(
            $"/api/archives/restore-me/insert-script?connectionId={conn}&table=people_copy", null, Ct);

        using var built = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var sql = built.RootElement.GetProperty("sql").GetString()!;

        Assert.Contains("INSERT INTO people_copy", sql);
        Assert.Contains("'ada'", sql);
        Assert.Equal(2, built.RootElement.GetProperty("rows").GetInt64());
    }

    [Fact]
    public async Task An_archive_can_be_deleted_and_is_then_gone()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/temporary",
            new { connectionId = conn, sql = "SELECT 1 AS n" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/archives/temporary", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync("/api/archives/temporary", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/archives/temporary", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_name_that_climbs_out_of_the_directory_cannot()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        await client.PostAsJsonAsync("/api/archives/..%2F..%2Fescaped",
            new { connectionId = conn, sql = "SELECT 1 AS n" }, Ct);

        // Whatever it was called, it landed inside the archive directory.
        Assert.Empty(Directory.GetFiles(_dir, "*.ndjson"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dir, "archives"), "*.ndjson"));
    }

    [Fact]
    public async Task A_request_with_neither_a_statement_nor_an_object_is_refused()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/archives/nothing",
            new { connectionId = conn }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_statement_that_fails_leaves_no_half_written_file()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/archives/broken",
            new { connectionId = conn, sql = "SELECT * FROM nope" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "archives")));
    }
}

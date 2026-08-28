using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.ClickHouse;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What is inside a JSON column, inferred from a sample. The interesting cases are all pure: a path
/// that is a string in one row and a number in the next, an array of objects, a null, and a row that
/// is not JSON at all.
public class JsonShapeTests
{
    private static JsonShapeReport Infer(params string[] documents) => JsonShape.Infer(documents);

    private static JsonPath Path(JsonShapeReport report, string path) =>
        Assert.Single(report.Paths, entry => entry.Path == path);

    [Fact]
    public void A_flat_object_becomes_its_keys()
    {
        var report = Infer("""{"id": 1, "name": "ada", "active": true}""");

        Assert.Equal(1, report.Parsed);
        Assert.Equal(["id", "name", "active"], report.Paths.Select(path => path.Path));
        Assert.Equal(["number"], Path(report, "id").Types);
        Assert.Equal(["string"], Path(report, "name").Types);
        Assert.Equal(["boolean"], Path(report, "active").Types);
    }

    [Fact]
    public void A_nested_object_becomes_a_dotted_path()
    {
        var report = Infer("""{"address": {"city": "london", "zip": "E1"}}""");

        Assert.Equal(["object"], Path(report, "address").Types);
        Assert.Equal("london", Path(report, "address.city").Example);
    }

    [Fact]
    public void An_array_folds_into_one_entry_rather_than_one_per_element()
    {
        var report = Infer("""{"tags": [{"name": "a"}, {"name": "b"}, {"name": "c"}]}""");

        // Not tags[0].name, tags[1].name…: that is a listing of a sample, not a shape.
        Assert.Equal(["array"], Path(report, "tags").Types);
        Assert.Equal(["object"], Path(report, "tags[]").Types);
        Assert.Equal(1, Path(report, "tags[].name").Present);
    }

    [Fact]
    public void A_path_with_two_types_says_both()
    {
        var report = Infer("""{"id": 1}""", """{"id": "two"}""");

        // This is exactly where a flatten breaks, so it is the thing to show.
        Assert.Equal(["number", "string"], Path(report, "id").Types);
        Assert.Equal(2, Path(report, "id").Present);
    }

    [Fact]
    public void A_path_missing_from_some_documents_is_counted_rather_than_hidden()
    {
        var report = Infer("""{"a": 1, "b": 2}""", """{"a": 1}""", """{"a": 1}""");

        Assert.Equal(3, Path(report, "a").Present);
        Assert.Equal(1, Path(report, "b").Present);
    }

    [Fact]
    public void A_null_is_a_type_of_its_own()
    {
        var report = Infer("""{"city": null}""", """{"city": "london"}""");

        Assert.Equal(["null", "string"], Path(report, "city").Types);
    }

    [Fact]
    public void A_row_that_is_not_json_is_reported_by_the_count_rather_than_by_an_error()
    {
        var report = Infer("""{"a": 1}""", "not json at all", """{"a": 2}""");

        Assert.Equal(3, report.Sampled);
        Assert.Equal(2, report.Parsed);
        Assert.Null(report.Note);
    }

    [Fact]
    public void A_column_with_nothing_readable_says_so()
    {
        Assert.Equal("none of the sampled rows is JSON", Infer("nope", "also nope").Note);
        Assert.Equal("nothing in this column to read", JsonShape.Infer([]).Note);
    }

    [Fact]
    public void A_long_example_is_cut_so_a_panel_can_show_it()
    {
        var report = Infer($"{{\"note\": \"{new string('x', 300)}\"}}");

        Assert.True(Path(report, "note").Example!.Length <= 61);
        Assert.EndsWith("…", Path(report, "note").Example);
    }

    [Fact]
    public void Depth_stops_somewhere()
    {
        // Ten levels deep is a document nobody flattens; the report stays readable.
        var document = string.Concat(Enumerable.Repeat("{\"a\":", 10)) + "1"
                       + new string('}', 10);

        var report = Infer(document);

        Assert.True(report.Paths.Count <= 7, report.Paths.Count.ToString());
    }
}

/// The same path, in each engine's own spelling. Pure: the point is the SQL, not the server.
public class JsonPathSqlTests
{
    [Fact]
    public void PostgreSql_reads_a_path_with_the_operator_people_recognise() =>
        Assert.Equal("\"payload\"::jsonb #>> '{address,city}'",
            JsonShape.Expression(new PostgreSqlDialect(), "payload", "address.city"));

    [Fact]
    public void And_an_array_step_takes_the_first_element() =>
        // A flattened column holds one value; "the first tag" is the useful one.
        Assert.Equal("\"payload\"::jsonb #>> '{tags,0,name}'",
            JsonShape.Expression(new PostgreSqlDialect(), "payload", "tags[].name"));

    [Fact]
    public void MySql_unquotes_what_it_extracts() =>
        Assert.Equal("JSON_UNQUOTE(JSON_EXTRACT(`payload`, '$.address.city'))",
            JsonShape.Expression(new MySqlDialect(), "payload", "address.city"));

    [Fact]
    public void SqlServer_uses_json_value() =>
        Assert.Equal("JSON_VALUE([payload], '$.address.city')",
            JsonShape.Expression(new SqlServerDialect(), "payload", "address.city"));

    [Fact]
    public void Sqlite_and_duckdb_take_the_standard_spelling() =>
        Assert.Equal("json_extract(\"payload\", '$.address.city')",
            JsonShape.Expression(new SqliteDialect(), "payload", "address.city"));

    [Fact]
    public void ClickHouse_takes_the_path_as_keys() =>
        Assert.Equal("JSONExtractString(`payload`, 'address', 'city')",
            JsonShape.Expression(new ClickHouseDialect(), "payload", "address.city"));

    [Fact]
    public void A_flatten_names_every_column_after_its_path()
    {
        var sql = JsonShape.FlattenSql(new PostgreSqlDialect(), "\"public\".\"events\"", "payload",
        [
            new JsonPath("id", ["number"], 2, "1"),
            new JsonPath("address.city", ["string"], 2, "london"),
            new JsonPath("tags", ["array"], 2, null),
            new JsonPath("tags[].name", ["string"], 1, "a"),
        ]);

        Assert.Contains("AS \"id\"", sql);
        Assert.Contains("AS \"address_city\"", sql);
        Assert.Contains("AS \"tags_name\"", sql);
        // The array's own path has no single value to select; its items do.
        Assert.DoesNotContain("AS \"tags\"", sql);
        Assert.DoesNotContain("AS \"address\"", sql);
        Assert.Contains("FROM \"public\".\"events\"", sql);
    }

    [Fact]
    public void A_column_with_no_paths_flattens_to_a_plain_select() =>
        Assert.Equal("SELECT * FROM \"t\"",
            JsonShape.FlattenSql(new PostgreSqlDialect(), "\"t\"", "payload", []));

    [Fact]
    public void A_document_of_nothing_but_objects_has_nothing_to_flatten() =>
        // An object is not a value; saying so beats a SELECT of JSON blobs nobody asked for.
        Assert.Equal("SELECT * FROM \"t\"",
            JsonShape.FlattenSql(new PostgreSqlDialect(), "\"t\"", "payload",
                [new JsonPath("user", ["object"], 1, null)]));

    [Fact]
    public void A_quote_in_a_key_cannot_end_the_path_literal() =>
        Assert.DoesNotContain("''''",
            JsonShape.Expression(new SqliteDialect(), "payload", "it's"));
}

/// End to end against PostgreSQL: the shape of a real jsonb column, and the flatten it produces
/// actually running.
public class JsonShapeEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-json").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE events (id int, payload jsonb);
            INSERT INTO events VALUES
              (1, '{"user": {"name": "ada", "city": "london"}, "tags": [{"name": "a"}], "n": 1}'),
              (2, '{"user": {"name": "grace"}, "tags": [], "n": "two"}'),
              (3, NULL);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_shape_of_a_jsonb_column_comes_back_with_its_paths_and_types()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var report = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{id}/json?ref=Table:public/events&column=payload", Ct)).RootElement;

        // The NULL row is not sampled at all, so two documents are parsed.
        Assert.Equal(2, report.GetProperty("parsed").GetInt32());

        var paths = report.GetProperty("paths").EnumerateArray()
            .ToDictionary(path => path.GetProperty("path").GetString()!, path => path);

        Assert.Equal(2, paths["user.name"].GetProperty("present").GetInt32());
        Assert.Equal(1, paths["user.city"].GetProperty("present").GetInt32());
        Assert.Equal(["number", "string"],
            paths["n"].GetProperty("types").EnumerateArray().Select(type => type.GetString()));
        Assert.True(paths.ContainsKey("tags[].name"));
    }

    [Fact]
    public async Task And_the_flatten_it_offers_runs_on_the_server()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var report = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{id}/json?ref=Table:public/events&column=payload", Ct)).RootElement;

        var flatten = report.GetProperty("flatten").GetString()!;

        // The SELECT the panel hands to a query tab has to be SQL this server accepts.
        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql = flatten, maxRows = 10,
        }, Ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("user_name", body);
        Assert.Contains("ada", body);
        Assert.DoesNotContain("\"error\"", body);
    }

    [Fact]
    public async Task A_column_that_is_not_there_is_refused()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync(
            $"/api/data/{id}/json?ref=Table:public/events&column=nope", Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}

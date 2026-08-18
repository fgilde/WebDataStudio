using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Import;

public class ImportEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-importapi").FullName;
    private string _source = "";
    private string _target = "";

    public async ValueTask InitializeAsync()
    {
        _source = Path.Combine(_dir, "source.db");
        _target = Path.Combine(_dir, "target.db");

        await Seed(_source, """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus');
            """);
        await Seed(_target, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");

        static async Task Seed(string path, string sql)
        {
            await using var db = new SqliteConnection($"Data Source={path}");
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SRC"] = $"sqlite:///{_source.Replace('\\', '/')}",
                ["WDS_CONN_DST"] = $"sqlite:///{_target.Replace('\\', '/')}",
            })));

    private static async Task<Dictionary<string, string>> IdsAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
    }

    private static MultipartFormDataContent Upload(string csv, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Add(file, "file", "people.csv");
        foreach (var (key, value) in fields) content.Add(new StringContent(value), key);
        return content;
    }

    [Fact]
    public async Task Preview_returns_columns_and_detected_types()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var response = await factory.CreateClient()
            .PostAsync("/api/import/preview", Upload("id,name\n7,ada\n8,linus\n"), ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("csv", body.GetProperty("format").GetString());
        Assert.Equal("id", body.GetProperty("columns")[0].GetString());
        Assert.Equal("integer", body.GetProperty("detectedTypes")[0].GetString());
    }

    [Fact]
    public async Task Execute_inserts_the_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.PostAsync("/api/import/execute",
            Upload("id,name\n7,ada\n8,linus\n", ("connectionId", ids["DST"]), ("table", "people")), ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
    }

    [Fact]
    public async Task Execute_reports_a_bad_row_and_keeps_the_good_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        // The middle row violates NOT NULL on name.
        var response = await client.PostAsync("/api/import/execute",
            Upload("id,name\n7,ada\n8,\n9,grace\n", ("connectionId", ids["DST"]), ("table", "people")), ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Execute_skips_a_column_mapped_to_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var mapping = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = "id", ["name"] = "name", ["extra"] = "",
        });

        var response = await client.PostAsync("/api/import/execute",
            Upload("id,name,extra\n7,ada,ignored\n",
                ("connectionId", ids["DST"]), ("table", "people"), ("mapping", mapping)), ct);

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("inserted").GetInt32());
    }

    [Fact]
    public async Task Rejects_a_file_whose_format_cannot_be_told()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("x"u8.ToArray()), "file", "mystery.docx");

        var response = await factory.CreateClient().PostAsync("/api/import/preview", content, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Copies_a_table_between_two_connections()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.PostAsJsonAsync("/api/copy-table", new
        {
            sourceConnectionId = ids["SRC"],
            sourceRef = "Table:main/people",
            targetConnectionId = ids["DST"],
            targetTable = "people",
        }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
    }
}

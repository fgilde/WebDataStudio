using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Export;

public class ExportEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-export").FullName;
    private string _dataDb = "";

    public async ValueTask InitializeAsync()
    {
        _dataDb = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_dataDb}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(bool readOnly = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_DEMO"] = $"sqlite:///{_dataDb.Replace('\\', '/')}",
                ["WDS_READONLY"] = readOnly ? "true" : null,
            })));

    private static async Task<string> ConnectionIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Lists_the_available_formats()
    {
        using var factory = Factory();
        var raw = await factory.CreateClient().GetStringAsync("/api/export/formats", TestContext.Current.CancellationToken);

        Assert.Contains("csv", raw);
        Assert.Contains("xlsx", raw);
        Assert.Contains("sql-insert", raw);
    }

    [Fact]
    public async Task Exports_a_query_as_csv_with_a_download_filename()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/csv", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT id, name FROM people ORDER BY id",
            scope = "result",
        }, ct);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? "");

        var csv = await response.Content.ReadAsStringAsync(ct);
        Assert.StartsWith("id,name", csv);
        Assert.Contains("1,ada", csv);
    }

    [Fact]
    public async Task Exports_a_table_by_reference_without_any_sql()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/json", new
        {
            connectionId = await ConnectionIdAsync(client),
            objectRef = "Table:main/people",
            scope = "table",
        }, ct);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("linus", json);
    }

    [Fact]
    public async Task Exporting_a_table_stays_allowed_on_a_read_only_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/csv", new
        {
            connectionId = await ConnectionIdAsync(client),
            objectRef = "Table:main/people",
            scope = "table",
        }, ct);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Rejects_an_unknown_format()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/wingdings", new
        {
            connectionId = await ConnectionIdAsync(client), sql = "SELECT 1", scope = "result",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_schema_scope_for_a_format_that_cannot_express_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/xlsx", new
        {
            connectionId = await ConnectionIdAsync(client), scope = "schema", schema = "main",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exports_a_whole_schema_as_sql()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export/sql-insert", new
        {
            connectionId = await ConnectionIdAsync(client), scope = "schema", schema = "main",
        }, ct);

        response.EnsureSuccessStatusCode();
        Assert.Contains("INSERT INTO", await response.Content.ReadAsStringAsync(ct));
    }
}

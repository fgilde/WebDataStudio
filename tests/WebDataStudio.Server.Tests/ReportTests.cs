using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What a report asks for, read out of the statement. Pure.
public class ReportParameterTests
{
    private static IReadOnlyList<string> Names(string sql) =>
        Reports.Parameters(sql, "postgresql");

    [Fact]
    public void The_parameters_are_named_in_the_order_they_are_asked() =>
        Assert.Equal(["from", "to"],
            Names("SELECT * FROM orders WHERE placed BETWEEN :from AND :to"));

    [Fact]
    public void The_same_one_twice_is_one_box() =>
        Assert.Equal(["month"],
            Names("SELECT * FROM a WHERE m = :month UNION SELECT * FROM b WHERE m = :month"));

    [Fact]
    public void A_comment_and_a_literal_hold_no_parameters()
    {
        Assert.Empty(Names("-- :note about this\nSELECT 1"));
        Assert.Empty(Names("/* :block */ SELECT 1"));
        Assert.Empty(Names("SELECT ':not_a_parameter' AS text"));
        Assert.Equal(["real"], Names("SELECT ':fake', :real"));
    }

    [Fact]
    public void A_cast_is_not_a_parameter() =>
        // `value::text` is two colons and a type, which is the trap this walk exists for.
        Assert.Empty(Names("SELECT value::text FROM settings"));

    [Fact]
    public void Every_engine_gets_the_marker_people_type_there()
    {
        // Not the one the provider needs on the wire: for PostgreSQL the dialect says @ while the
        // editor offers :, and Npgsql accepts both.
        Assert.Equal(["from"],
            Reports.Parameters("SELECT * FROM t WHERE a = $from", "sqlite"));
        Assert.Equal(["from"],
            Reports.Parameters("SELECT * FROM t WHERE a = @from", "sqlserver"));
        Assert.Equal(["from"],
            Reports.Parameters("SELECT * FROM t WHERE a = :from", "oracle"));

        // An at sign in PostgreSQL is an operator, not a marker.
        Assert.Empty(Reports.Parameters("SELECT * FROM t WHERE a = @from", "postgresql"));

        // ClickHouse writes {name:Type}, and a key/value store has nothing to bind into.
        Assert.Empty(Reports.Parameters("SELECT {a:String}", "clickhouse"));
        Assert.Empty(Reports.Parameters("GET :key", "redis"));
    }
}

/// End to end: a saved query becomes a form, and running it is reading only.
public class ReportTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-reports").FullName;
    private string _db = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer TEXT, placed TEXT, total REAL);
            INSERT INTO orders VALUES
              (1, 'ada',   '2026-06-02', 10.0),
              (2, 'grace', '2026-06-19', 20.0),
              (3, 'linus', '2026-07-04', 30.0);
            """;
        await command.ExecuteNonQueryAsync(Ct);
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
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<string> SaveAsync(HttpClient client, string connectionId, string name,
        string sql)
    {
        var response = await client.PostAsJsonAsync("/api/saved-queries", new
        {
            id = "", name, folder = "Sales", sql, connectionId,
        }, Ct);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_saved_query_with_a_connection_is_a_report()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var connectionId = await IdAsync(client);

        var id = await SaveAsync(client, connectionId, "Orders in a range",
            "SELECT customer, total FROM orders WHERE placed BETWEEN $from AND $to ORDER BY placed");

        var reports = await client.GetFromJsonAsync<JsonElement>("/api/reports", Ct);
        var report = reports.EnumerateArray().Single(one => one.GetProperty("id").GetString() == id);

        Assert.Equal("Orders in a range", report.GetProperty("name").GetString());
        Assert.Equal("Sales", report.GetProperty("folder").GetString());
        Assert.Equal(["from", "to"],
            report.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    public async Task Running_one_answers_with_rows()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var connectionId = await IdAsync(client);

        var id = await SaveAsync(client, connectionId, "Orders in a range",
            "SELECT customer, total FROM orders WHERE placed BETWEEN $from AND $to ORDER BY placed");

        var response = await client.PostAsJsonAsync($"/api/reports/{id}/run", new
        {
            parameters = new Dictionary<string, string> { ["from"] = "2026-06-01", ["to"] = "2026-06-30" },
        }, Ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
        Assert.Equal("customer",
            result.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_missing_value_is_said_rather_than_run_as_null()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var connectionId = await IdAsync(client);

        var id = await SaveAsync(client, connectionId, "Orders in a range",
            "SELECT * FROM orders WHERE placed BETWEEN $from AND $to");

        var response = await client.PostAsJsonAsync($"/api/reports/{id}/run", new
        {
            parameters = new Dictionary<string, string> { ["from"] = "2026-06-01" },
        }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("to", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_saved_query_that_changes_data_is_not_offered_as_a_report()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var connectionId = await IdAsync(client);

        var id = await SaveAsync(client, connectionId, "Tidy up",
            "DELETE FROM orders WHERE placed < $before");

        // It is listed — it is still a saved query — but pressing it is refused: a report is what
        // somebody who is not reading the SQL is going to press.
        var response = await client.PostAsJsonAsync($"/api/reports/{id}/run", new
        {
            parameters = new Dictionary<string, string> { ["before"] = "2026-01-01" },
        }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("changes data", await response.Content.ReadAsStringAsync(Ct));

        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync(Ct);
        await using var count = db.CreateCommand();
        count.CommandText = "SELECT count(*) FROM orders";
        Assert.Equal(3L, Convert.ToInt64(await count.ExecuteScalarAsync(Ct)));
    }

    [Fact]
    public async Task A_saved_query_with_no_connection_is_not_a_report()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/saved-queries", new
        {
            id = "", name = "Nowhere", folder = (string?)null, sql = "SELECT 1",
            connectionId = (string?)null,
        }, Ct)).EnsureSuccessStatusCode();

        var reports = await client.GetFromJsonAsync<JsonElement>("/api/reports", Ct);

        // Nothing to run it against.
        Assert.DoesNotContain(reports.EnumerateArray(),
            report => report.GetProperty("name").GetString() == "Nowhere");
    }

    [Fact]
    public async Task A_report_nobody_has_heard_of_is_a_404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/reports/nope/run",
            new { parameters = new Dictionary<string, string>() }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Which statements take everything. Pure: the reading is the risky part, not the running.
public class SweepingStatementTests
{
    private static readonly PostgreSqlDialect Dialect = new();

    private static IReadOnlyList<string> Sweeping(string sql) => SafetyNet.Sweeping(sql, Dialect);

    [Theory]
    [InlineData("DELETE FROM orders", "orders")]
    [InlineData("delete orders;", "orders")]
    [InlineData("UPDATE orders SET status = 'new'", "orders")]
    [InlineData("TRUNCATE TABLE orders", "orders")]
    [InlineData("TRUNCATE orders", "orders")]
    [InlineData("DELETE FROM public.orders;", "public.orders")]
    public void A_statement_with_no_condition_takes_the_whole_table(string sql, string table) =>
        Assert.Equal([table], Sweeping(sql));

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("UPDATE orders SET status = 'new' WHERE id = 1")]
    [InlineData("SELECT * FROM orders")]
    [InlineData("INSERT INTO orders (id) VALUES (1)")]
    [InlineData("DROP TABLE orders")]
    public void And_a_statement_that_is_specific_is_left_alone(string sql) =>
        // Reading a table nobody asked to read is its own kind of surprise.
        Assert.Empty(Sweeping(sql));

    [Fact]
    public void A_comment_is_not_a_statement_and_a_literal_is_not_a_clause()
    {
        Assert.Empty(Sweeping("-- DELETE FROM orders\nSELECT 1"));

        // The WHERE is inside a string, so this statement really has none.
        Assert.Equal(["orders"], Sweeping("DELETE FROM orders -- WHERE id = 1"));
        Assert.Equal(["notes"], Sweeping("UPDATE notes SET body = 'WHERE it went'"));
    }

    [Fact]
    public void Every_table_a_script_sweeps_is_named_once()
    {
        var tables = Sweeping("""
            DELETE FROM order_items;
            DELETE FROM orders;
            DELETE FROM orders;
            DELETE FROM customers WHERE id = 9;
            """);

        Assert.Equal(["order_items", "orders"], tables);
    }

    [Fact]
    public void Off_is_off()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WDS_SAFETY_NET"] = "false" }).Build();

        Assert.False(SafetyOptions.FromConfiguration(config).Enabled);
        Assert.Equal(20_000, SafetyOptions.FromConfiguration(
            new ConfigurationBuilder().Build()).MaxRows);
    }

    [Fact]
    public void What_was_kept_reads_as_a_sentence()
    {
        Assert.Equal("3 row(s) of orders were kept as the archive 'orders-before-x'",
            new KeptRows("orders-before-x", "orders", 3, false).Describe());

        Assert.Contains("there were more",
            new KeptRows("orders-before-x", "orders", 20_000, true).Describe());
    }
}

/// End to end: the statement runs, and the rows are in an archive.
public class SafetyNetTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-safety").FullName;
    private string _db = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer TEXT, total REAL);
            INSERT INTO orders VALUES (1, 'ada', 10.0), (2, 'grace', 20.0), (3, 'linus', 30.0);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(bool safety = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, $"wds-{safety}.db"),
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
                ["WDS_ARCHIVE_DIR"] = Path.Combine(_dir, $"archives-{safety}"),
                ["WDS_SAFETY_NET"] = safety ? null : "false",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<string> RunAsync(HttpClient client, string id, string sql)
    {
        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql, maxRows = 100,
        }, Ct);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(Ct);
    }

    [Fact]
    public async Task A_delete_with_no_where_keeps_the_rows_first()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var stream = await RunAsync(client, id, "DELETE FROM orders");

        // Said before the statement ran, in the stream the editor already renders.
        Assert.Contains("row(s) of orders were kept as the archive", stream);

        var archives = await client.GetFromJsonAsync<JsonElement>("/api/archives", Ct);
        var kept = archives.GetProperty("items").EnumerateArray()
            .First(archive => archive.GetProperty("name").GetString()!.StartsWith("orders-before-"));

        Assert.Equal(3, kept.GetProperty("rows").GetInt64());

        // And the rows really are gone: the copy is not a refusal.
        var after = await RunAsync(client, id, "SELECT count(*) AS n FROM orders");
        Assert.Contains("\"rows\":[[0]]", after.Replace(" ", ""));

        // The archive can be scripted back out as inserts, which is what makes this a way back.
        var name = kept.GetProperty("name").GetString();
        var script = await client.PostAsync(
            $"/api/archives/{name}/insert-script?table=orders&connectionId={id}", null, Ct);
        script.EnsureSuccessStatusCode();
        Assert.Contains("INSERT INTO", await script.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_statement_that_is_specific_keeps_nothing()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var stream = await RunAsync(client, id, "DELETE FROM orders WHERE id = 1");

        Assert.DoesNotContain("were kept as the archive", stream);

        var archives = await client.GetFromJsonAsync<JsonElement>("/api/archives", Ct);
        Assert.Empty(archives.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Turned_off_it_keeps_nothing_either()
    {
        using var factory = Factory(safety: false);
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var stream = await RunAsync(client, id, "DELETE FROM orders");

        Assert.DoesNotContain("were kept as the archive", stream);

        var archives = await client.GetFromJsonAsync<JsonElement>("/api/archives", Ct);
        Assert.Empty(archives.GetProperty("items").EnumerateArray());
    }
}

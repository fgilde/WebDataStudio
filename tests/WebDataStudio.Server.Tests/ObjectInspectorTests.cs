using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests;

/// The three questions pgAdmin answers on an object's tabs and the studio did not: how big is it and
/// who reads it, who may do what to it, and what it depends on.
public class ObjectInspectorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-inspector").FullName;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, city text);
            CREATE INDEX ix_customers_city ON customers(city);
            CREATE TABLE orders (id serial PRIMARY KEY,
                                 customer_id integer NOT NULL REFERENCES customers(id),
                                 total numeric(10,2));
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;

            INSERT INTO customers (name, city)
            SELECT 'customer ' || i, CASE WHEN i % 2 = 0 THEN 'london' ELSE 'lisbon' END
              FROM generate_series(1, 500) AS i;

            CREATE ROLE reporting;
            GRANT SELECT ON customers TO reporting;

            ANALYZE customers;
            """;
        await command.ExecuteNonQueryAsync();
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
                ["WDS_CONN_SHOP"] = _container.GetConnectionString(),
                ["WDS_CONN_SHOP_ENGINE"] = "postgresql",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Statistics_report_the_size_the_rows_and_the_indexes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/statistics?ref=Table:public/customers", ct);

        Assert.True(body.GetProperty("supported").GetBoolean());

        var byName = body.GetProperty("table").EnumerateArray()
            .ToDictionary(row => row.GetProperty("name").GetString()!,
                          row => row.GetProperty("value").GetString());

        Assert.Contains("Total size", byName.Keys);
        Assert.Contains("Dead rows", byName.Keys);
        Assert.Contains("Last analyze", byName.Keys);
        // 500 rows were inserted and the table was analysed, so the estimate is real.
        Assert.Equal("500", byName["Live rows (estimate)"]);

        var indexes = body.GetProperty("indexes").EnumerateArray()
            .Select(index => index.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("ix_customers_city", indexes);
        Assert.Contains("customers_pkey", indexes);
        Assert.Contains(body.GetProperty("indexes").EnumerateArray(),
            index => index.GetProperty("primary").GetBoolean());
    }

    /// SQLite answers none of this, and empty rows would read as "this table has no indexes".
    [Fact]
    public async Task An_engine_that_cannot_answer_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var sqlite = Path.Combine(_dir, "local.db");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqlite}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(ct);
        }

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds-sqlite.db"),
                ["WDS_CONN_LOCAL"] = "sqlite:///" + sqlite.Replace(Path.DirectorySeparatorChar, '/'),
            })));

        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/statistics?ref=Table:main/t", ct);

        Assert.False(body.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Privileges_list_who_may_do_what()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/privileges?ref=Table:public/customers", ct);

        Assert.True(body.GetProperty("supported").GetBoolean());

        var grants = body.GetProperty("grants").EnumerateArray()
            .Select(grant => (
                Grantee: grant.GetProperty("grantee").GetString(),
                Privilege: grant.GetProperty("privilege").GetString()))
            .ToList();

        Assert.Contains(("reporting", "SELECT"), grants);
        Assert.Contains("SELECT", body.GetProperty("privileges").EnumerateArray()
            .Select(privilege => privilege.GetString()));
    }

    /// A GRANT is a change like any other: the studio hands over the statement and the existing
    /// preview runs it.
    [Fact]
    public async Task A_grant_comes_back_as_a_statement_and_goes_through_the_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var built = await client.PostAsJsonAsync(
            $"/api/schema/{id}/privileges/statement?ref=Table:public/customers",
            new { grantee = "reporting", privilege = "INSERT" }, ct);

        built.EnsureSuccessStatusCode();
        var sql = (await built.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!;

        Assert.Equal("GRANT INSERT ON \"public\".\"customers\" TO \"reporting\";", sql);

        // Nothing ran yet: the grant is not there until the script is applied.
        var before = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/privileges?ref=Table:public/customers", ct);

        Assert.DoesNotContain(before.GetProperty("grants").EnumerateArray(),
            grant => grant.GetProperty("privilege").GetString() == "INSERT"
                     && grant.GetProperty("grantee").GetString() == "reporting");

        var preview = await client.PostAsJsonAsync($"/api/ddl/{id}/script/preview", new { sql }, ct);
        preview.EnsureSuccessStatusCode();
        var hash = (await preview.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("hash").GetString();

        var applied = await client.PostAsJsonAsync($"/api/ddl/{id}/apply", new { hash }, ct);
        applied.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/privileges?ref=Table:public/customers", ct);

        Assert.Contains(after.GetProperty("grants").EnumerateArray(),
            grant => grant.GetProperty("privilege").GetString() == "INSERT"
                     && grant.GetProperty("grantee").GetString() == "reporting");
    }

    [Fact]
    public async Task A_revoke_reads_as_a_revoke_and_a_nonsense_privilege_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var revoke = await client.PostAsJsonAsync(
            $"/api/schema/{id}/privileges/statement?ref=Table:public/customers",
            new { grantee = "reporting", privilege = "SELECT", revoke = true }, ct);

        var sql = (await revoke.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString();
        Assert.StartsWith("REVOKE SELECT ON", sql);

        var nonsense = await client.PostAsJsonAsync(
            $"/api/schema/{id}/privileges/statement?ref=Table:public/customers",
            new { grantee = "reporting", privilege = "DROP EVERYTHING" }, ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, nonsense.StatusCode);
    }

    [Fact]
    public async Task Dependencies_say_what_a_view_reads_and_what_reads_a_table()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var view = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ddl/{id}/dependencies?ref=View:public/big_orders", ct);
        var table = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ddl/{id}/dependencies?ref=Table:public/customers", ct);

        var uses = view.GetProperty("dependsOn").EnumerateArray().Select(v => v.GetString()!).ToList();
        var usedBy = table.GetProperty("usedBy").EnumerateArray().Select(v => v.GetString()!).ToList();

        Assert.Contains(uses, name => name.Contains("orders"));
        Assert.Contains(usedBy, name => name.Contains("orders"));
    }

    /// One analyser, in Analysis/PlanRules, reached through /api/query/analyze — the plan endpoint
    /// hands over the plan and nothing else.
    [Fact]
    public async Task The_plan_is_read_by_the_analysis_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/query/analyze", new
        {
            connectionId = id,
            sql = "SELECT c.name, count(*) FROM customers c JOIN orders o ON o.customer_id = c.id GROUP BY 1",
            actual = false,
        }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var summary = body.GetProperty("summary");

        Assert.True(summary.GetProperty("nodeCount").GetInt32() > 1);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("plan").ValueKind);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Analysis;

/// What a suggested index is, and what the two plans are said to mean. Pure.
public class IndexTrialSqlTests
{
    [Theory]
    [InlineData("CREATE INDEX ix_orders_person ON orders (person_id)", "orders", "person_id", false)]
    [InlineData("create unique index ix on public.orders(a, b)", "public.orders", "a, b", true)]
    [InlineData("CREATE INDEX IF NOT EXISTS ix ON t (a)", "t", "a", false)]
    public void A_create_index_is_read_apart(string ddl, string table, string columns, bool unique)
    {
        var parsed = IndexTrial.Parse(ddl);

        Assert.NotNull(parsed);
        Assert.Equal(table, parsed!.Value.Table);
        Assert.Equal(columns, parsed.Value.Columns);
        Assert.Equal(unique, parsed.Value.Unique);
    }

    [Theory]
    [InlineData("DROP INDEX ix")]
    [InlineData("VACUUM (ANALYZE) orders")]
    [InlineData("ALTER TABLE orders ADD COLUMN x int")]
    [InlineData("SELECT 1")]
    public void And_anything_else_is_not_tried(string ddl) =>
        // The studio only tries what it can undo.
        Assert.Null(IndexTrial.Parse(ddl));

    private static PlanNode Node(string operation, double? cost, params PlanNode[] children) =>
        new(operation, null, cost, null, null, null, children, []);

    [Fact]
    public void A_cheaper_plan_says_how_much_cheaper()
    {
        var verdict = IndexTrial.Describe(Node("Seq Scan", 1000), Node("Index Scan", 40), 1000, 40);

        Assert.Contains("cheaper by 96%", verdict);
        Assert.Contains("stopped scanning the table", verdict);
    }

    [Fact]
    public void An_index_that_changes_nothing_says_that_rather_than_a_number() =>
        Assert.Equal("no real difference — this index is not the answer",
            IndexTrial.Describe(Node("Index Scan", 100), Node("Index Scan", 99), 100, 99));

    [Fact]
    public void An_index_that_costs_more_says_so() =>
        Assert.Contains("more expensive by 50%",
            IndexTrial.Describe(Node("Seq Scan", 100), Node("Seq Scan", 150), 100, 150));

    [Fact]
    public void And_where_the_engine_reports_no_cost_the_operations_still_do()
    {
        Assert.Equal("it stopped scanning the table",
            IndexTrial.Describe(Node("SCAN TABLE orders", null), Node("SEARCH TABLE orders", null),
                null, null));

        Assert.Equal("no visible difference in the plan",
            IndexTrial.Describe(Node("SEARCH TABLE orders", null), Node("SEARCH TABLE orders", null),
                null, null));
    }

    [Fact]
    public void A_scan_deeper_in_the_plan_counts_too() =>
        Assert.Contains("stopped scanning",
            IndexTrial.Describe(
                Node("Aggregate", 1000, Node("Seq Scan", 900)),
                Node("Aggregate", 30, Node("Index Scan", 20)),
                1000, 30));
}

/// The trial itself, against a real table: the index is created, measured and dropped again.
public class IndexTrialTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-trial").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();

        // Enough rows that the planner prefers an index when there is one.
        command.CommandText = """
            CREATE TABLE page_views (
                id bigserial PRIMARY KEY, path text NOT NULL, ms int NOT NULL);

            INSERT INTO page_views (path, ms)
            SELECT '/p' || (n % 500), n % 400 FROM generate_series(1, 50000) AS n;

            ANALYZE page_views;
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(bool readOnly = false, string? color = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, $"wds-{readOnly}-{color}.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
                ["WDS_CONN_PG_READONLY"] = readOnly ? "true" : null,
                ["WDS_CONN_PG_COLOR"] = color,
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_index_is_created_measured_and_dropped_again()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync($"/api/analyze/{id}/try-index", new
        {
            sql = "SELECT * FROM page_views WHERE path = '/p42'",
            ddl = "CREATE INDEX ix_page_views_path ON page_views (path)",
        }, Ct);

        response.EnsureSuccessStatusCode();
        var trial = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // The planner really did change its mind: a sequential scan over 50 000 rows became a
        // lookup.
        Assert.Contains("Seq Scan", trial.GetProperty("before").GetProperty("operation").GetString());
        Assert.DoesNotContain("Seq Scan",
            trial.GetProperty("after").GetProperty("operation").GetString()!);
        Assert.Contains("cheaper by", trial.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, trial.GetProperty("leftBehind").ValueKind);

        // And nothing was left behind: a trial that leaves an index is a trial nobody runs twice.
        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var check = db.CreateCommand();
        check.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'page_views' AND indexname LIKE 'wds_trial%'";

        Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync(Ct)));
    }

    [Fact]
    public async Task An_index_that_does_not_help_says_so()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        // The primary key already answers this one.
        var trial = await (await client.PostAsJsonAsync($"/api/analyze/{id}/try-index", new
        {
            sql = "SELECT * FROM page_views WHERE id = 42",
            ddl = "CREATE INDEX ix_page_views_ms ON page_views (ms)",
        }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Contains("no ", trial.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Something_that_is_not_a_create_index_is_refused()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/analyze/{await IdAsync(client)}/try-index",
            new { sql = "SELECT 1", ddl = "VACUUM (ANALYZE) page_views" }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_only_connection_is_refused_and_says_why()
    {
        using var factory = Factory(readOnly: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/analyze/{await IdAsync(client)}/try-index",
            new
            {
                sql = "SELECT * FROM page_views WHERE path = '/p42'",
                ddl = "CREATE INDEX ix ON page_views (path)",
            }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("read-only", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task And_so_is_production()
    {
        using var factory = Factory(color: "red");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/analyze/{await IdAsync(client)}/try-index",
            new
            {
                sql = "SELECT * FROM page_views WHERE path = '/p42'",
                ddl = "CREATE INDEX ix ON page_views (path)",
            }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("production", await response.Content.ReadAsStringAsync(Ct));
    }
}

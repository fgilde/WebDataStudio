using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A widget's statement running for real: the range and the variables reach it, and everything else
/// about the path is the one a query tab uses.
public class DashboardRunTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-widget").FullName;
    private string _db = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE orders (id INTEGER PRIMARY KEY, region TEXT, placed_at TEXT);
            INSERT INTO orders VALUES
                (1, 'eu', '2026-09-10T10:00:00.0000000+00:00'),
                (2, 'eu', '2026-09-09T10:00:00.0000000+00:00'),
                (3, 'us', '2026-01-01T10:00:00.0000000+00:00');
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                ["WDS_CONN_WAREHOUSE"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
            })));

    private static async Task<string> IdAsync(HttpClient client, string name = "SHOP")
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));

        return document.RootElement.EnumerateArray()
            .First(one => one.GetProperty("name").GetString() == name)
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> RunAsync(HttpClient client, object body, string path = "/api/query/execute")
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// The range is bound and the rows outside it stay where they are.
    [Fact]
    public async Task A_widget_reads_its_dashboards_range()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var ndjson = await RunAsync(client, new
        {
            connectionId = id,
            sql = "SELECT count(*) AS n FROM orders WHERE $__timeFilter(placed_at)",
            dashboard = new { from = "2026-09-10T00:00:00Z", to = "2026-09-11T00:00:00Z" },
        });

        Assert.Contains("\"rows\":[[1]]", ndjson);
    }

    [Fact]
    public async Task A_variable_reaches_the_statement_as_a_value()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var ndjson = await RunAsync(client, new
        {
            connectionId = id,
            sql = "SELECT count(*) AS n FROM orders WHERE region = $region",
            dashboard = new
            {
                from = "now-10y", to = "now",
                variables = new Dictionary<string, string[]> { ["region"] = ["eu"] },
            },
        });

        Assert.Contains("\"rows\":[[2]]", ndjson);
    }

    [Fact]
    public async Task A_list_of_values_reaches_it_as_a_list()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var ndjson = await RunAsync(client, new
        {
            connectionId = id,
            sql = "SELECT count(*) AS n FROM orders WHERE region IN (${region:csv})",
            dashboard = new
            {
                from = "now-10y", to = "now",
                variables = new Dictionary<string, string[]> { ["region"] = ["eu", "us"] },
            },
        });

        Assert.Contains("\"rows\":[[3]]", ndjson);
    }

    /// A value that could carry a second statement is refused, and the refusal arrives the way
    /// every other query error does — in the stream, naming the variable.
    [Fact]
    public async Task A_value_that_cannot_be_a_value_is_refused_in_the_stream()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var ndjson = await RunAsync(client, new
        {
            connectionId = id,
            sql = "SELECT * FROM orders WHERE region IN (${region:csv})",
            dashboard = new
            {
                from = "now-1d", to = "now",
                variables = new Dictionary<string, string[]>
                {
                    ["region"] = ["eu'\nUNION ALL SELECT * FROM orders"],
                },
            },
        });

        Assert.Contains("\"type\":\"error\"", ndjson);
        Assert.Contains("region", ndjson);
    }

    /// Without a dashboard nothing is expanded: a macro in a query tab is text a person typed, and
    /// it stays text.
    [Fact]
    public async Task A_query_tab_is_left_exactly_as_it_was()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var plain = await RunAsync(client, new { connectionId = id, sql = "SELECT count(*) AS n FROM orders" });
        Assert.Contains("\"rows\":[[3]]", plain);

        var tab = await RunAsync(client, new { connectionId = id, sql = "SELECT '$__from' AS t" });
        Assert.Contains("$__from", tab);

        var widget = await RunAsync(client, new
        {
            connectionId = id,
            sql = "SELECT '$__from' AS t",
            dashboard = new { from = "2026-09-10T00:00:00Z", to = "2026-09-11T00:00:00Z" },
        });

        Assert.DoesNotContain("$__from", widget);
        Assert.Contains(
            DateTimeOffset.Parse("2026-09-10T00:00:00Z").ToUnixTimeMilliseconds().ToString(),
            widget);
    }

    /// A widget over two connections is the studio's own federation, and the macros reach every
    /// statement in it — each with its own engine's dialect.
    [Fact]
    public async Task A_widget_over_two_connections_reads_the_range_as_well()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var ndjson = await RunAsync(client, new
        {
            sources = new[]
            {
                new
                {
                    connectionId = await IdAsync(client),
                    sql = "SELECT region, count(*) AS n FROM orders WHERE $__timeFilter(placed_at) GROUP BY region",
                    alias = "recent",
                },
                new
                {
                    connectionId = await IdAsync(client, "WAREHOUSE"),
                    sql = "SELECT region, count(*) AS total FROM orders GROUP BY region",
                    alias = "all_time",
                },
            },
            sql = "SELECT a.region, r.n, a.total FROM all_time a LEFT JOIN recent r ON r.region = a.region ORDER BY a.region",
            dashboard = new { from = "2026-09-01T00:00:00Z", to = "2026-09-11T00:00:00Z" },
        }, "/api/federate/run");

        // eu has two orders in the range and two in all time; us has one in all time and none in it.
        Assert.Contains("\"rows\":[[\"eu\",2,2],[\"us\",null,1]]", ndjson);
    }
}

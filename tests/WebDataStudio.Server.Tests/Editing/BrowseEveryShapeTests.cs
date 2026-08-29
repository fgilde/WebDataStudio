using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests.Editing;

/// Opening a table is the first thing anybody does, and the demo has more shapes of "table" than
/// the unit tests do: a partitioned one, a materialised view, a view, a table with no key at all.
///
/// This walks the whole schema through the same endpoint the data tab uses and asks of every one of
/// them: does it answer, and is what it says about editing true?
public class BrowseEveryShapeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-shapes").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            -- an ordinary table with a key
            CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL);
            INSERT INTO customers (name) VALUES ('ada'), ('grace');

            -- one whose only identity is a unique index over a non-nullable column
            CREATE TABLE codes (code text NOT NULL, label text);
            CREATE UNIQUE INDEX ux_codes ON codes(code);
            INSERT INTO codes VALUES ('de', 'Germany');

            -- and one with nothing at all: the heap table every old schema has
            CREATE TABLE notes (body text);
            INSERT INTO notes VALUES ('first'), ('second');

            -- a view, a materialised view
            CREATE VIEW customer_names AS SELECT name FROM customers;
            CREATE MATERIALIZED VIEW customer_count AS SELECT count(*) AS n FROM customers;

            -- a partitioned table and its partitions
            CREATE TABLE readings (id bigint, taken date, value numeric)
                PARTITION BY RANGE (taken);
            CREATE TABLE readings_q1 PARTITION OF readings
                FOR VALUES FROM ('2026-01-01') TO ('2026-04-01');
            CREATE TABLE readings_q2 PARTITION OF readings
                FOR VALUES FROM ('2026-04-01') TO ('2026-07-01');
            INSERT INTO readings VALUES (1, '2026-02-01', 1.5), (2, '2026-05-01', 2.5);
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

    private static async Task<JsonElement> BrowseAsync(HttpClient client, string conn, string objectRef)
    {
        var response = await client.GetAsync(
            $"/api/data/{conn}?ref={Uri.EscapeDataString(objectRef)}&limit=5", Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{objectRef} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string[] Columns(JsonElement page) =>
        [.. page.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Fact]
    public async Task Every_shape_in_the_schema_opens()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        string[] shapes =
        [
            "Table:public/customers", "Table:public/codes", "Table:public/notes",
            "Table:public/readings", "Table:public/readings_q1",
            "View:public/customer_names", "MaterializedView:public/customer_count",
        ];

        // The assertion is the status code inside BrowseAsync: a shape that answers 400 is the bug
        // this test exists for.
        foreach (var shape in shapes) await BrowseAsync(client, conn, shape);
    }

    [Fact]
    public async Task A_key_a_unique_index_and_a_physical_address_in_that_order()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var withKey = await BrowseAsync(client, conn, "Table:public/customers");
        Assert.Equal(["id"], withKey.GetProperty("keyColumns").EnumerateArray().Select(k => k.GetString()));
        Assert.DoesNotContain("wds_row_address", Columns(withKey));

        var withIndex = await BrowseAsync(client, conn, "Table:public/codes");
        Assert.Equal(["code"], withIndex.GetProperty("keyColumns").EnumerateArray().Select(k => k.GetString()));
        Assert.DoesNotContain("wds_row_address", Columns(withIndex));

        var withNeither = await BrowseAsync(client, conn, "Table:public/notes");
        Assert.True(withNeither.GetProperty("editable").GetBoolean());
        Assert.Contains("wds_row_address", Columns(withNeither));
    }

    [Fact]
    public async Task A_partitioned_root_is_read_and_not_written()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var page = await BrowseAsync(client, conn, "Table:public/readings");

        // Its rows live in the partitions, and two partitions can hand out the same ctid — so an
        // update by one would eventually write the wrong row. It reads; it does not offer editing.
        Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
        Assert.False(page.GetProperty("editable").GetBoolean());
        Assert.DoesNotContain("wds_row_address", Columns(page));
    }

    [Fact]
    public async Task A_partition_itself_is_an_ordinary_table()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var page = await BrowseAsync(client, conn, "Table:public/readings_q1");

        // One partition is a heap with no key: addressing a row inside it is unambiguous.
        Assert.True(page.GetProperty("editable").GetBoolean());
        Assert.Contains("wds_row_address", Columns(page));
    }

    [Fact]
    public async Task A_materialised_view_is_read_and_not_written()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var page = await BrowseAsync(client, conn, "MaterializedView:public/customer_count");

        // The engine refuses an UPDATE on one; it is refreshed instead.
        Assert.False(page.GetProperty("editable").GetBoolean());
        Assert.DoesNotContain("wds_row_address", Columns(page));
    }

    [Fact]
    public async Task A_keyless_table_can_be_sorted_filtered_and_paged_with_its_address_along()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // The address is added to the projection, so everything that wraps the projection — the
        // ORDER BY, the WHERE, the LIMIT/OFFSET — has to keep working.
        var response = await client.GetAsync(
            $"/api/data/{conn}?ref={Uri.EscapeDataString("Table:public/notes")}"
            + "&sort=body&desc=true&filterColumn=body&filter=first&offset=0&limit=1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, page.GetProperty("rows").GetArrayLength());
    }
}

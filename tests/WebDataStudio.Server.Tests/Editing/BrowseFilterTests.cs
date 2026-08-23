using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Editing;

/// The filter language, the distinct-value list and the borrowed columns, through the endpoint that
/// serves the data tab. The language itself is checked against the shared corpus in
/// FilterExpressionTests; this is about it arriving intact.
public class BrowseFilterTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-browse").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT,
                                    api_token TEXT);
            INSERT INTO customers VALUES
                (1,'Ada','Berlin','tok-1'),
                (2,'Linus','Lisbon','tok-2'),
                (3,'Grace',NULL,'tok-3');

            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL,
                                 total NUMERIC NOT NULL, placed TEXT,
                                 FOREIGN KEY (customer_id) REFERENCES customers(id));
            INSERT INTO orders VALUES
                (1,1,10.5,'2026-08-23 09:00:00'),
                (2,1,99.0,'2026-01-02 09:00:00'),
                (3,2,5.0,'2026-08-23 22:00:00');
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
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    private const string Customers = "Table%3Amain%2Fcustomers";
    private const string Orders = "Table%3Amain%2Forders";

    private static async Task<JsonDocument> GetAsync(HttpClient client, string url) =>
        JsonDocument.Parse(await client.GetStringAsync(url, TestContext.Current.CancellationToken));

    private static List<string> Column(JsonDocument page, int index) =>
        [.. page.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row[index].ToString())];

    [Fact]
    public async Task A_plain_word_still_means_contains()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Customers}&filterColumn=name&filter=ad");

        Assert.Equal(["Ada"], Column(page, 1));
    }

    [Fact]
    public async Task Text_is_compared_without_case_on_every_engine()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // PostgreSQL's LIKE is case-sensitive and MySQL's is not; the studio's filter is neither.
        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Customers}&filterColumn=name&filter=ADA");

        Assert.Equal(["Ada"], Column(page, 1));
    }

    [Fact]
    public async Task The_operators_reach_the_statement()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var starts = await GetAsync(client,
            $"/api/data/{conn}?ref={Customers}&filterColumn=name&filter=%5EL");
        Assert.Equal(["Linus"], Column(starts, 1));

        using var not = await GetAsync(client,
            $"/api/data/{conn}?ref={Customers}&filterColumn=name&filter=~a");
        Assert.Equal(["Linus"], Column(not, 1));

        using var missing = await GetAsync(client,
            $"/api/data/{conn}?ref={Customers}&filterColumn=city&filter=NULL");
        Assert.Equal(["Grace"], Column(missing, 1));
    }

    [Fact]
    public async Task A_number_is_compared_as_a_number_rather_than_as_text()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // As text, "10.5" sorts before "5"; as a number it does not.
        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&filterColumn=total&filter=%3E9");

        Assert.Equal(2, page.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Whitespace_is_and_and_a_comma_is_or()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var between = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&filterColumn=total&filter=%3E6%20%3C50");
        Assert.Single(between.RootElement.GetProperty("rows").EnumerateArray());

        using var either = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&filterColumn=total&filter=%3D5%2C%3D99");
        Assert.Equal(2, either.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task A_value_in_the_filter_cannot_become_sql()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.GetAsync(
            $"/api/data/{conn}?ref={Customers}&filterColumn=name&filter=%27%3B%20DROP%20TABLE%20customers%3B%20--",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Still there, and still three rows.
        using var page = await GetAsync(client, $"/api/data/{conn}?ref={Customers}");
        Assert.Equal(3, page.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Distinct_values_come_back_most_common_first_with_their_counts()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var found = await GetAsync(client,
            $"/api/data/{conn}/distinct?ref={Orders}&column=customer_id");

        var values = found.RootElement.GetProperty("values").EnumerateArray().ToList();

        Assert.False(found.RootElement.GetProperty("masked").GetBoolean());
        Assert.Equal("1", values[0].GetProperty("value").ToString());
        Assert.Equal(2, values[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task The_distinct_list_of_a_masked_column_is_refused_rather_than_counted()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // The distinct values of a column of secrets are the secrets.
        using var found = await GetAsync(client,
            $"/api/data/{conn}/distinct?ref={Customers}&column=api_token");

        Assert.True(found.RootElement.GetProperty("masked").GetBoolean());
        Assert.Empty(found.RootElement.GetProperty("values").EnumerateArray());
    }

    [Fact]
    public async Task A_column_that_is_not_there_is_refused()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.GetAsync(
            $"/api/data/{conn}/distinct?ref={Customers}&column=nope",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_column_can_be_borrowed_from_the_table_a_key_points_at()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&lookup=customer_id.name&sort=id");

        var names = page.RootElement.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()).ToList();

        Assert.Contains("customer_id.name", names);
        Assert.Equal(["customer_id.name"],
            page.RootElement.GetProperty("lookups").EnumerateArray()
                .Select(entry => entry.GetString()).ToList());

        // The borrowed value is the one on the other side of the key.
        var row = page.RootElement.GetProperty("rows")[0];
        Assert.Equal("Ada", row[names.IndexOf("customer_id.name")].GetString());
    }

    [Fact]
    public async Task Filtering_and_sorting_still_address_the_right_table_once_something_is_joined()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // `id` exists on both sides; unqualified it would be ambiguous.
        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&lookup=customer_id.name&sort=id&desc=true"
            + "&filterColumn=id&filter=%3E1");

        Assert.Equal(2, page.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal("3", page.RootElement.GetProperty("rows")[0][0].ToString());
    }

    [Fact]
    public async Task A_lookup_that_names_nothing_real_is_dropped_rather_than_written_into_sql()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        using var page = await GetAsync(client,
            $"/api/data/{conn}?ref={Orders}&lookup=nope.name&lookup=customer_id.nope"
            + "&lookup=%22%3B%20DROP%20TABLE%20orders%3B%20--.x");

        Assert.Empty(page.RootElement.GetProperty("lookups").EnumerateArray());
        Assert.Equal(3, page.RootElement.GetProperty("rows").GetArrayLength());
    }
}

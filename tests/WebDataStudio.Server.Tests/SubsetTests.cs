using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests;

/// A small, loadable, anonymised copy of a real database — and then the proof that it loads.
public class SubsetTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-subset").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE countries (code text PRIMARY KEY, title text);
            INSERT INTO countries VALUES ('de', 'Germany'), ('pt', 'Portugal'), ('ke', 'Kenya');

            CREATE TABLE customers (
                id int PRIMARY KEY,
                country_code text REFERENCES countries(code),
                name text,
                email text,
                city text,
                api_token text,
                salary numeric);
            INSERT INTO customers VALUES
              (1, 'de', 'Erika Mustermann', 'erika@real.example', 'Berlin', 'sk-live-1', 51000),
              (2, 'pt', 'João Silva', 'joao@real.example', 'Porto', 'sk-live-2', 47000),
              (3, 'ke', 'Amina Otieno', 'amina@real.example', 'Nairobi', 'sk-live-3', 39000);

            CREATE TABLE orders (
                id int PRIMARY KEY,
                customer_id int REFERENCES customers(id),
                total numeric,
                placed date);
            INSERT INTO orders
            SELECT n, 1 + (n % 3), n * 10, '2026-01-01'::date + n
            FROM generate_series(1, 40) AS n;

            -- Nothing points at this one, so nothing should drag it into a subset.
            CREATE TABLE audit_log (id int PRIMARY KEY, text text);
            INSERT INTO audit_log VALUES (1, 'not part of any subset');
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

    private static async Task<JsonElement> SubsetAsync(HttpClient client, string id, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/export/subset/{id}", body, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task The_rows_come_with_the_rows_they_point_at()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var subset = await SubsetAsync(client, await IdAsync(client), new
        {
            table = "orders", rows = 5,
        });

        var tables = subset.GetProperty("tables").EnumerateArray()
            .Select(table => table.GetProperty("name").GetString()).ToList();

        // Five orders, the customers they belong to, and the countries those customers are in.
        Assert.Equal(["countries", "customers", "orders"], tables.Order().ToList());
        // Parents before children, or the script does not load.
        Assert.True(tables.IndexOf("countries") < tables.IndexOf("customers"));
        Assert.True(tables.IndexOf("customers") < tables.IndexOf("orders"));
        // Nothing referenced it, so it is not here.
        Assert.DoesNotContain("audit_log", tables);

        var script = subset.GetProperty("script").GetString()!;
        Assert.Contains("CREATE TABLE", script);
        Assert.Contains("INSERT INTO \"public\".\"orders\"", script);
        Assert.Contains("WDS_SEED_SQL", script);
    }

    [Fact]
    public async Task What_is_about_people_is_replaced_and_the_keys_are_not()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var script = (await SubsetAsync(client, await IdAsync(client), new
        {
            table = "customers", rows = 3,
        })).GetProperty("script").GetString()!;

        // Nothing real about a person survives.
        Assert.DoesNotContain("Erika Mustermann", script);
        Assert.DoesNotContain("erika@real.example", script);
        Assert.DoesNotContain("Berlin", script);
        // A secret is gone rather than plausible.
        Assert.DoesNotContain("sk-live-1", script);
        Assert.Contains("redacted", script);
        // The keys are what makes the subset loadable, so they are untouched.
        Assert.Contains("'de'", script);
        Assert.Contains("example.com", script);
    }

    [Fact]
    public async Task The_same_value_is_replaced_the_same_way_everywhere()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var first = (await SubsetAsync(client, id, new { table = "customers", rows = 3 }))
            .GetProperty("script").GetString()!;
        var second = (await SubsetAsync(client, id, new { table = "customers", rows = 3 }))
            .GetProperty("script").GetString()!;

        // Two runs of the same subset produce the same names: a dataset that changes every time
        // cannot be talked about in a bug report.
        Assert.Equal(Rows(first), Rows(second));
    }

    private static List<string> Rows(string script) =>
        [.. script.Split('\n').Where(line => line.StartsWith("  ("))];

    [Fact]
    public async Task Asking_for_the_real_thing_says_so_in_the_script()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var script = (await SubsetAsync(client, await IdAsync(client), new
        {
            table = "customers", rows = 1, anonymise = false, includeSchema = false,
        })).GetProperty("script").GetString()!;

        Assert.Contains("NOT anonymised", script);
        Assert.Contains("Erika Mustermann", script);
        Assert.DoesNotContain("CREATE TABLE", script);
    }

    [Fact]
    public async Task A_condition_decides_which_rows_to_start_from()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var subset = await SubsetAsync(client, await IdAsync(client), new
        {
            table = "orders", rows = 100, where = "total > 350",
        });

        var orders = subset.GetProperty("tables").EnumerateArray()
            .First(table => table.GetProperty("name").GetString() == "orders");

        // 36 of the 40 orders have a total above 350.
        Assert.Equal(5, orders.GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task Following_nothing_takes_one_table()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var subset = await SubsetAsync(client, await IdAsync(client), new
        {
            table = "orders", rows = 3, depth = 0,
        });

        Assert.Single(subset.GetProperty("tables").EnumerateArray());
    }

    [Fact]
    public async Task A_table_nobody_has_heard_of_is_a_bad_request()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/export/subset/{await IdAsync(client)}",
            new { table = "nope" }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task And_the_script_loads_into_an_empty_database()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var script = (await SubsetAsync(client, await IdAsync(client), new
        {
            table = "orders", rows = 10,
        })).GetProperty("script").GetString()!;

        // The whole point: a subset that does not load is a text file.
        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);

        await using (var schema = db.CreateCommand())
        {
            schema.CommandText = "CREATE SCHEMA subset_target; SET search_path TO subset_target";
            await schema.ExecuteNonQueryAsync(Ct);
        }

        await using var load = db.CreateCommand();
        // The script names the source schema; loading it somewhere else is a search_path away.
        load.CommandText = script.Replace("\"public\".", "\"subset_target\".");
        await load.ExecuteNonQueryAsync(Ct);

        await using var count = db.CreateCommand();
        count.CommandText = "SELECT count(*) FROM subset_target.orders";
        Assert.Equal(10L, Convert.ToInt64(await count.ExecuteScalarAsync(Ct)));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The transaction a query tab holds open across requests: BEGIN, look at what the statements did,
/// and only then commit — or roll the whole thing back.
public class TransactionModeTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-tx").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people (name) VALUES ('ada');
            """;
        await command.ExecuteNonQueryAsync();
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
                ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    /// The execute endpoint answers in NDJSON; for these tests only "did it fail" matters.
    private static async Task<string> RunAsync(HttpClient client, object body)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/api/query/execute", body, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<long> CountAsync(HttpClient client, string conn)
    {
        var text = await RunAsync(client, new
        {
            connectionId = conn, sql = "SELECT count(*) AS n FROM people",
        });

        // The rows chunk is the only place a number appears in this little result.
        var line = text.Split('\n').First(l => l.Contains("\"rows\""));
        using var document = JsonDocument.Parse(line);

        return document.RootElement.GetProperty("rows")[0][0].GetInt64();
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var begun = await (await client.PostAsJsonAsync("/api/tx/begin", new { connectionId = conn }, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        var id = begun.GetProperty("id").GetString()!;

        await RunAsync(client, new
        {
            connectionId = conn, sql = "INSERT INTO people (name) VALUES ('grace')", transactionId = id,
        });

        // Inside the transaction it is there.
        var inside = await RunAsync(client, new
        {
            connectionId = conn, sql = "SELECT count(*) AS n FROM people", transactionId = id,
        });

        Assert.Contains("2", inside);

        var rolled = await client.PostAsJsonAsync($"/api/tx/{id}/rollback", new { }, ct);
        rolled.EnsureSuccessStatusCode();

        Assert.Equal(1, await CountAsync(client, conn));
    }

    [Fact]
    public async Task A_committed_transaction_keeps_what_it_did()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var begun = await (await client.PostAsJsonAsync("/api/tx/begin", new { connectionId = conn }, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        var id = begun.GetProperty("id").GetString()!;

        await RunAsync(client, new
        {
            connectionId = conn, sql = "INSERT INTO people (name) VALUES ('grace')", transactionId = id,
        });

        (await client.PostAsJsonAsync($"/api/tx/{id}/commit", new { }, ct)).EnsureSuccessStatusCode();

        Assert.Equal(2, await CountAsync(client, conn));
    }

    /// The point of showing them: a transaction cannot be forgotten quietly.
    [Fact]
    public async Task What_is_open_is_listed_with_what_it_has_run()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var begun = await (await client.PostAsJsonAsync("/api/tx/begin", new { connectionId = conn }, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        var id = begun.GetProperty("id").GetString()!;

        await RunAsync(client, new
        {
            connectionId = conn, sql = "UPDATE people SET name = 'ada2'", transactionId = id,
        });

        var open = await client.GetFromJsonAsync<JsonElement>("/api/tx", ct);
        var one = Assert.Single(open.GetProperty("open").EnumerateArray().ToList());

        Assert.Equal(id, one.GetProperty("id").GetString());
        Assert.Equal(1, one.GetProperty("statements").GetInt32());
        Assert.Contains("UPDATE", one.GetProperty("lastStatement").GetString()!);
        Assert.True(open.GetProperty("idleTimeoutSeconds").GetInt32() > 0);

        await client.PostAsJsonAsync($"/api/tx/{id}/rollback", new { }, ct);
    }

    [Fact]
    public async Task A_transaction_that_is_no_longer_open_says_so_rather_than_running_anyway()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = conn, sql = "DELETE FROM people", transactionId = "not-a-transaction",
        }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountAsync(client, conn));

        // Closing one twice is the same answer rather than a 500.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/tx/not-a-transaction/commit", new { }, ct)).StatusCode);
    }

    /// Off by default, because stopping at the first error is what a migration wants. On, the other
    /// ninety-eight inserts of a hundred should not be lost to two duplicates.
    [Fact]
    public async Task Keeping_going_after_an_error_is_asked_for_rather_than_assumed()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var script = """
            INSERT INTO people (name) VALUES ('one');
            INSERT INTO nothing_here (name) VALUES ('boom');
            INSERT INTO people (name) VALUES ('three');
            """;

        await RunAsync(client, new { connectionId = conn, sql = script });
        Assert.Equal(2, await CountAsync(client, conn));   // 'one' ran, the rest stopped

        await RunAsync(client, new { connectionId = conn, sql = script, continueOnError = true });
        Assert.Equal(4, await CountAsync(client, conn));   // 'one' and 'three' both ran
    }
}

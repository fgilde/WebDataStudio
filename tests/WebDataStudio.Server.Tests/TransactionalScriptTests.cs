using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class TransactionalScriptTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-tx").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);";
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
                ["WDS_CONNECTIONS"] = JsonSerializer.Serialize(new[]
                {
                    new { name = "SHOP", engine = "sqlite", connectionString = $"Data Source={_db}" },
                }),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private async Task<long> CountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM people";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> RunAsync(HttpClient client, string id, string sql, bool transactional)
    {
        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql, transactional,
        }, TestContext.Current.CancellationToken);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_failing_script_rolls_back_everything_before_it()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await RunAsync(client, id,
            "INSERT INTO people VALUES (1,'ada'); INSERT INTO people VALUES (1,'clash');", true);

        Assert.Contains("error", body);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Without_the_flag_the_rows_before_the_failure_stay()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await RunAsync(client, id,
            "INSERT INTO people VALUES (2,'ada'); INSERT INTO people VALUES (2,'clash');", false);

        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task A_successful_script_commits()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await RunAsync(client, id,
            "INSERT INTO people VALUES (3,'ada'); INSERT INTO people VALUES (4,'linus');", true);

        Assert.Equal(2, await CountAsync());
    }
}

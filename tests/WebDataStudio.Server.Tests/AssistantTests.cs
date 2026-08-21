using System.Net.Http.Json;
using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The optional assistance. Two things matter more than the answers: it does not exist unless it is
/// configured, and what it sends is a statement or a question — never a row of data.
public class AssistantTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-assist").FullName;
    private readonly List<string> _sent = [];

    private WebApplication? _stub;
    private string _stubUrl = "";
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using (var connection = new SqliteConnection($"Data Source={_db}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT, secret_token TEXT);
                INSERT INTO customers VALUES (1, 'ada', 'tok-42');
                """;
            await command.ExecuteNonQueryAsync();
        }

        // A stand-in for an OpenAI-compatible endpoint: it records what it was sent and answers in
        // the shape the real one does.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));

        _stub = builder.Build();
        _stub.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            _sent.Add(await reader.ReadToEndAsync());

            return Results.Ok(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "It counts the rows.\n\n```sql\nSELECT count(*) FROM customers\n```",
                        },
                    },
                },
            });
        });

        await _stub.StartAsync();
        _stubUrl = _stub.Urls.First() + "/v1/chat/completions";
    }

    public async ValueTask DisposeAsync()
    {
        if (_stub is not null) await _stub.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(bool configured) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                if (configured)
                {
                    settings["WDS_ASSIST_ENDPOINT"] = _stubUrl;
                    settings["WDS_ASSIST_KEY"] = "test-key";
                    settings["WDS_ASSIST_MODEL"] = "stub-model";
                }

                c.AddInMemoryCollection(settings);
            }));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Without_configuration_the_feature_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: false);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var explain = await client.PostAsJsonAsync("/api/assist/explain",
            new { connectionId = id, sql = "SELECT 1" }, ct);
        var draft = await client.PostAsJsonAsync("/api/assist/sql",
            new { connectionId = id, question = "how many customers?" }, ct);

        Assert.Equal(HttpStatusCode.NotImplemented, explain.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, draft.StatusCode);

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health", ct);
        Assert.False(health.GetProperty("assist").GetBoolean());
        // Nothing was called, because there is nothing to call.
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task With_configuration_health_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);

        var health = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", ct);

        Assert.True(health.GetProperty("assist").GetBoolean());
    }

    [Fact]
    public async Task An_explanation_comes_back_with_its_statements_as_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/assist/explain",
            new { connectionId = id, sql = "SELECT count(*) FROM customers" }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("counts the rows", body.GetProperty("text").GetString());
        var statement = Assert.Single(body.GetProperty("statements").EnumerateArray());
        Assert.Equal("SELECT count(*) FROM customers", statement.GetString());
    }

    /// The whole point of the flag: a schema is a list of names, and it travels only when asked for.
    [Fact]
    public async Task The_schema_travels_only_when_it_is_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await client.PostAsJsonAsync("/api/assist/sql",
            new { connectionId = id, question = "count them", includeSchema = false }, ct);

        Assert.DoesNotContain("customers(", _sent[^1]);

        await client.PostAsJsonAsync("/api/assist/sql",
            new { connectionId = id, question = "count them", includeSchema = true }, ct);

        Assert.Contains("customers(", _sent[^1]);
        Assert.Contains("secret_token", _sent[^1]);
    }

    /// A column called `secret_token` may travel as a name — that is what a schema is — but its
    /// value must never leave the machine.
    [Fact]
    public async Task No_row_of_data_is_ever_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await client.PostAsJsonAsync("/api/assist/sql",
            new { connectionId = id, question = "who is in there?", includeSchema = true }, ct);

        Assert.DoesNotContain("tok-42", _sent[^1]);
        Assert.DoesNotContain("ada", _sent[^1]);
    }

    [Fact]
    public async Task A_request_with_nothing_in_it_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var explain = await client.PostAsJsonAsync("/api/assist/explain",
            new { connectionId = id, sql = "" }, ct);
        var draft = await client.PostAsJsonAsync("/api/assist/sql",
            new { connectionId = id, question = "  " }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, explain.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, draft.StatusCode);
    }

    [Fact]
    public async Task The_configured_model_and_key_are_used()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: true);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        await client.PostAsJsonAsync("/api/assist/explain",
            new { connectionId = id, sql = "SELECT 1" }, ct);

        Assert.Contains("stub-model", _sent[^1]);
    }
}

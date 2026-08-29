using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests;

/// PostgreSQL's own message bus, end to end: something sends, the studio hears it.
public class NotifyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-notify").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(bool readOnly = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
                ["WDS_CONN_PG_READONLY"] = readOnly ? "true" : "false",
                ["WDS_CONN_LOCAL"] = $"sqlite:///{Path.Combine(_dir, "x.db").Replace('\\', '/')}",
            })));

    private static async Task<Dictionary<string, string>> IdsAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
    }

    /// Reads server-sent events until one arrives, or until the wait is over. The stream never
    /// ends on its own — that is the point of it — so the caller says how long it is worth waiting.
    private static async Task<JsonElement?> FirstEventAsync(HttpClient client, string url,
        Func<Task> after, TimeSpan wait)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(wait);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancel.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
            using var reader = new StreamReader(stream);

            // The listener is registered by the time the response headers are out, but the LISTEN
            // itself runs a moment later; sending immediately would race it.
            await Task.Delay(500, cancel.Token);
            await after();

            while (await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                return JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone();
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived in time, which the assertion below reports better than a timeout does.
        }

        return null;
    }

    [Fact]
    public async Task Hears_a_notification_that_something_else_sent()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var arrived = await FirstEventAsync(client,
            $"/api/notify/{ids["PG"]}/listen?channels=jobs",
            async () =>
            {
                // Somebody else's connection entirely: this is the application, not the studio.
                await using var db = new NpgsqlConnection(_container.GetConnectionString());
                await db.OpenAsync(Ct);
                await using var command = db.CreateCommand();
                command.CommandText = "SELECT pg_notify('jobs', 'a job is waiting')";
                await command.ExecuteNonQueryAsync(Ct);
            },
            TimeSpan.FromSeconds(20));

        Assert.NotNull(arrived);
        Assert.Equal("jobs", arrived!.Value.GetProperty("channel").GetString());
        Assert.Equal("a job is waiting", arrived.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Hears_what_the_studio_itself_sent()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var arrived = await FirstEventAsync(client,
            $"/api/notify/{ids["PG"]}/listen?channels=MixedCase",
            async () =>
            {
                var response = await client.PostAsJsonAsync($"/api/notify/{ids["PG"]}/send",
                    new { channel = "MixedCase", payload = "hello" }, Ct);
                response.EnsureSuccessStatusCode();
            },
            TimeSpan.FromSeconds(20));

        // A channel is an identifier: quoting it keeps MixedCase from being folded to mixedcase,
        // which is what an application that spells it that way is listening on.
        Assert.NotNull(arrived);
        Assert.Equal("MixedCase", arrived!.Value.GetProperty("channel").GetString());
        Assert.Equal("hello", arrived.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_engine_without_it_says_so_rather_than_hanging()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.GetAsync($"/api/notify/{ids["LOCAL"]}/listen?channels=jobs",
            HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("LISTEN/NOTIFY", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_read_only_connection_can_listen_and_not_send()
    {
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.PostAsJsonAsync($"/api/notify/{ids["PG"]}/send",
            new { channel = "jobs", payload = "nope" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Listening_on_nothing_is_refused()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.GetAsync($"/api/notify/{ids["PG"]}/listen?channels=",
            HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

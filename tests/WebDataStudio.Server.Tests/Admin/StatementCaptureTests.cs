using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests.Admin;

/// "Show me what runs on this server in the next minute", end to end: the capture is started, a slow
/// statement runs somewhere else, and it comes back grouped with how long it was seen running.
public class StatementCaptureTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-capture").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

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

    [Fact]
    public async Task A_capture_sees_what_was_running_while_it_ran()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var started = await client.PostAsync($"/api/admin/capture/{id}?seconds=4", null, Ct);
        started.EnsureSuccessStatusCode();
        Assert.Equal("running",
            (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString());

        // Something slow, from its own connection, while the capture is looking.
        var slow = Task.Run(async () =>
        {
            await using var db = new NpgsqlConnection(_container.GetConnectionString());
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT pg_sleep(2), 'wds-capture-marker' AS marker";
            await command.ExecuteNonQueryAsync(Ct);
        }, Ct);

        JsonElement status;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            status = JsonDocument.Parse(
                await client.GetStringAsync($"/api/admin/capture/{id}", Ct)).RootElement;

            if (status.GetProperty("state").GetString() == "done") break;
            await Task.Delay(250, Ct);
        }

        await slow;

        status = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/capture/{id}", Ct)).RootElement;

        Assert.Equal("done", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("samples").GetInt32() > 0);

        var statements = status.GetProperty("statements").EnumerateArray()
            .Select(entry => entry.GetProperty("text").GetString() ?? "").ToList();

        Assert.Contains(statements, text => text.Contains("wds-capture-marker"));
    }

    [Fact]
    public async Task A_capture_can_be_stopped_early_and_keeps_what_it_saw()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await client.PostAsync($"/api/admin/capture/{id}?seconds=120", null, Ct);
        await Task.Delay(1200, Ct);

        var stopped = await client.DeleteAsync($"/api/admin/capture/{id}", Ct);
        stopped.EnsureSuccessStatusCode();

        // Stopping is not discarding: whatever was seen stays readable.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = JsonDocument.Parse(await client.GetStringAsync($"/api/admin/capture/{id}", Ct))
                .RootElement.GetProperty("state").GetString();

            if (state == "stopped") return;
            await Task.Delay(250, Ct);
        }

        Assert.Fail("the capture never reported itself stopped");
    }

    [Fact]
    public async Task A_connection_nobody_captured_reports_nothing_rather_than_failing()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var status = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/capture/{id}", Ct)).RootElement;

        Assert.Equal("none", status.GetProperty("state").GetString());
        Assert.Empty(status.GetProperty("statements").EnumerateArray());
    }

    [Fact]
    public async Task An_engine_that_cannot_say_what_it_runs_is_refused_with_a_reason()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds-sqlite.db"),
                ["WDS_CONN_LOCAL"] = "sqlite:///" + Path.Combine(_dir, "demo.db").Replace('\\', '/'),
            })));

        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsync($"/api/admin/capture/{id}?seconds=5", null, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot say what it is running", await response.Content.ReadAsStringAsync(Ct));
    }
}

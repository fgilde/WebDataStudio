using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The health report already existed; the only thing missing was somebody looking at it. What
/// matters here: nothing is posted unless a webhook was configured, and the same finding is not
/// posted twice.
public class HealthAlertTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-alerts").FullName;
    private readonly List<string> _posted = [];

    private WebApplication? _hook;
    private string _hookUrl = "";
    private string _db = "";
    private HttpStatusCode _answer = HttpStatusCode.OK;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using (var connection = new SqliteConnection($"Data Source={_db}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // No primary key: the SQLite analyzer reports that as a warning, which is exactly the
            // kind of finding worth a message.
            command.CommandText = "CREATE TABLE events (name TEXT, at TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));

        _hook = builder.Build();
        _hook.MapPost("/hook", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            _posted.Add(await reader.ReadToEndAsync());

            return Results.StatusCode((int)_answer);
        });

        await _hook.StartAsync();
        _hookUrl = _hook.Urls.First() + "/hook";
    }

    public async ValueTask DisposeAsync()
    {
        if (_hook is not null) await _hook.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                foreach (var (key, value) in extra) settings[key] = value;
                c.AddInMemoryCollection(settings);
            }));

    [Fact]
    public async Task Without_a_webhook_nothing_is_watched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var alerts = factory.Services.GetRequiredService<HealthAlerts>();
        var sent = await alerts.SweepAsync(ct);

        Assert.Equal(0, sent);
        Assert.Empty(_posted);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/health", ct));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("alerts").ValueKind);
    }

    [Fact]
    public async Task A_finding_is_posted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_ALERT_WEBHOOK", _hookUrl));
        var alerts = factory.Services.GetRequiredService<HealthAlerts>();

        var first = await alerts.SweepAsync(ct);
        var second = await alerts.SweepAsync(ct);

        Assert.True(first > 0, "the sweep found nothing to report");
        // The same finding an hour later is a message people learn to ignore.
        Assert.Equal(0, second);
        Assert.Single(_posted);

        using var document = JsonDocument.Parse(_posted[0]);
        var root = document.RootElement;

        // `text` is what Slack and friends read…
        Assert.Contains("SHOP", root.GetProperty("text").GetString());
        Assert.Contains("no primary key", root.GetProperty("text").GetString());
        // …and the structured half is there for anything that wants more.
        Assert.Equal("SHOP", root.GetProperty("connection").GetProperty("name").GetString());
        Assert.NotEmpty(root.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task Health_says_that_something_is_watching()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_ALERT_WEBHOOK", _hookUrl), ("WDS_ALERT_INTERVAL_MINUTES", "5"));

        using var document = JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/api/health", ct));
        var alerts = document.RootElement.GetProperty("alerts");

        Assert.Equal(5, alerts.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal("warning", alerts.GetProperty("minSeverity").GetString());
    }

    /// A findings list nobody received is not a findings list anybody has seen: the next sweep
    /// tries again rather than swallowing it.
    [Fact]
    public async Task A_failed_post_is_retried_next_time()
    {
        var ct = TestContext.Current.CancellationToken;
        _answer = HttpStatusCode.InternalServerError;

        using var factory = Factory(("WDS_ALERT_WEBHOOK", _hookUrl));
        var alerts = factory.Services.GetRequiredService<HealthAlerts>();

        Assert.Equal(0, await alerts.SweepAsync(ct));

        _answer = HttpStatusCode.OK;
        Assert.True(await alerts.SweepAsync(ct) > 0);
        Assert.Equal(2, _posted.Count);
    }

    [Fact]
    public async Task Only_named_connections_are_watched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_ALERT_WEBHOOK", _hookUrl), ("WDS_ALERT_CONNECTIONS", "SOMETHING_ELSE"));

        Assert.Equal(0, await factory.Services.GetRequiredService<HealthAlerts>().SweepAsync(ct));
        Assert.Empty(_posted);
    }

    /// `critical` and worse means the warnings this database has are not worth a message.
    [Fact]
    public async Task The_severity_floor_is_respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_ALERT_WEBHOOK", _hookUrl), ("WDS_ALERT_MIN_SEVERITY", "critical"));

        Assert.Equal(0, await factory.Services.GetRequiredService<HealthAlerts>().SweepAsync(ct));
        Assert.Empty(_posted);
    }
}

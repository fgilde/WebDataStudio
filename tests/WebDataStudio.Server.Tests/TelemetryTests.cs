using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A slow query in the studio and a slow query in the app are the same kind of problem, and the
/// second is already instrumented. What matters here: nothing is exported unless a collector is
/// configured, and the instrumentation itself is always there to be listened to.
public class TelemetryTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-otel").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO customers VALUES (1, 'ada'), (2, 'grace');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
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

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public void Options_are_read_from_the_standard_variables()
    {
        var configured = TelemetryOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4317",
                ["OTEL_SERVICE_NAME"] = "studio-a",
            }).Build());

        var off = TelemetryOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(configured.Configured);
        Assert.Equal("studio-a", configured.ServiceName);
        Assert.False(off.Configured);
        // A name that nobody set still has to be something.
        Assert.Equal("webdatastudio", off.ServiceName);
    }

    [Fact]
    public async Task Health_says_where_the_traces_go()
    {
        var ct = TestContext.Current.CancellationToken;

        using var off = Factory();
        var without = await off.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", ct);
        Assert.Equal(JsonValueKind.Null, without.GetProperty("telemetry").ValueKind);

        using var on = Factory(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"));
        var with = await on.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", ct);

        Assert.Equal("http://localhost:4317",
            with.GetProperty("telemetry").GetProperty("endpoint").GetString());
    }

    /// The span exists whether or not anybody exports it — that is what makes it listenable.
    [Fact]
    public async Task A_run_produces_a_span_and_its_numbers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var measured = new List<(string Name, long Value)>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, active) =>
            {
                if (instrument.Meter.Name == Telemetry.SourceName) active.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            measured.Add((instrument.Name, value)));
        meters.Start();

        var response = await client.PostAsJsonAsync("/api/query/execute",
            new { connectionId = id, sql = "SELECT name FROM customers" }, ct);
        await response.Content.ReadAsStringAsync(ct);

        var span = Assert.Single(spans, activity => activity.OperationName == "query.execute");
        Assert.Equal("sqlite", span.GetTagItem("engine"));
        Assert.Equal(2L, span.GetTagItem("rows"));
        Assert.Equal(false, span.GetTagItem("failed"));

        Assert.Contains(("wds.queries", 1L), measured);
        Assert.Contains(("wds.rows", 2L), measured);
    }

    [Fact]
    public async Task A_tool_call_is_counted_with_its_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var counted = new List<(string Tool, bool Failed)>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, active) =>
            {
                if (instrument.Name == "wds.tool.calls") active.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tool = "";
            var failed = false;

            foreach (var tag in tags)
            {
                if (tag.Key == "tool") tool = tag.Value?.ToString() ?? "";
                if (tag.Key == "failed") failed = tag.Value is true;
            }

            counted.Add((tool, failed));
        });
        meters.Start();

        await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "list_connections", arguments = new { } },
        }, ct);

        await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "describe_object", arguments = new { connectionId = "nope", @ref = "Table:x/y" } },
        }, ct);

        Assert.Contains(("list_connections", false), counted);
        Assert.Contains(("describe_object", true), counted);
    }
}

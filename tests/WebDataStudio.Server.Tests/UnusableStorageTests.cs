using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A data directory the studio cannot use is a deployment reality, not a bug: Azure Container Apps
/// mounts a volume as an Azure Files share, and SQLite on SMB either crawls or hangs. It used to
/// take the whole studio with it — both stores are singletons, so the first request that touched
/// one hung and every later one queued behind it, which showed up as a pending call and then a
/// 500 on /api/connections while /api/connections/test still worked.
///
/// The studio now starts, keeps the connections that came from the environment, and says what is
/// wrong instead.
public class UnusableStorageTests
{
    /// A path no process can create a directory for, on either platform.
    private static string UnusablePath() =>
        OperatingSystem.IsWindows() ? @"Z:\nowhere\wds.db" : "/proc/nowhere/wds.db";

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = UnusablePath(),
                ["WDS_CONN_SHOP"] = "sqlite:///tmp/shop.db",
            })));

    [Fact]
    public async Task The_studio_still_starts_and_health_says_what_is_wrong()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("degraded", body.RootElement.GetProperty("status").GetString());

        var store = body.RootElement.GetProperty("store");
        Assert.False(store.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(store.GetProperty("error").GetString()));
        // The path is the whole point of the message: it names what to fix.
        Assert.Contains("wds.db", store.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Connections_from_the_environment_are_still_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/connections", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains(body.EnumerateArray(), c => c.GetProperty("name").GetString() == "SHOP");
    }

    [Fact]
    public async Task Storing_a_connection_answers_503_with_the_path_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/connections", new
        {
            name = "manual",
            engine = "sqlite",
            connectionString = "Data Source=/tmp/manual.db",
            readOnly = false,
        }, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("wds.db", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_stuck_directory_does_not_hold_a_request_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        // The failure mode this exists for is a hang, so the assertion is about time, not content.
        var started = DateTimeOffset.UtcNow;
        await client.GetAsync("/api/connections", ct);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"the request took {elapsed}");
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class HealthTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-health").FullName;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    /// Health reports the storage state too, so it needs a data directory it can use — the default
    /// is /data, which exists in the container and not on a test machine.
    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    [Fact]
    public async Task Health_returns_ok_with_a_version_and_a_usable_store()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var response = await factory.CreateClient().GetAsync("/api/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));
        Assert.True(body.RootElement.GetProperty("store").GetProperty("available").GetBoolean());
    }
}

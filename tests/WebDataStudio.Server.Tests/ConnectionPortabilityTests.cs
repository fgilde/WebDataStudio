using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class ConnectionPortabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-portable").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private static object Definition(string name) => new
    {
        name,
        engine = "postgresql",
        connectionString = "Host=db.example;Port=5432;Username=me;Password=s3cret;Database=shop",
        readOnly = false,
    };

    [Fact]
    public async Task An_export_carries_no_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var raw = await client.GetStringAsync("/api/connections/export", ct);

        Assert.Contains("db.example", raw);
        Assert.DoesNotContain("s3cret", raw);
        Assert.DoesNotContain("connectionString", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_import_recreates_the_definitions_without_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var exported = await client.GetFromJsonAsync<JsonElement>("/api/connections/export", ct);
        (await client.DeleteAsync(
            $"/api/connections/{(await Ids(client))["prod"]}", ct)).EnsureSuccessStatusCode();

        var result = await (await client.PostAsJsonAsync("/api/connections/import", exported, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("prod", result.GetProperty("imported")[0].GetString());

        var summary = (await client.GetFromJsonAsync<JsonElement>("/api/connections", ct))
            .EnumerateArray().Single().GetProperty("summary").GetString();
        Assert.Contains("db.example", summary);
    }

    [Fact]
    public async Task A_duplicate_name_is_reported_without_aborting_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var payload = new[]
        {
            new { name = "prod", engine = "postgresql", readOnly = false, host = "x", database = "y" },
            new { name = "staging", engine = "postgresql", readOnly = false, host = "x", database = "y" },
        };

        var result = await (await client.PostAsJsonAsync("/api/connections/import", payload, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("staging", result.GetProperty("imported")[0].GetString());
        Assert.Equal("prod", result.GetProperty("skipped")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_unknown_engine_is_skipped_rather_than_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var payload = new[] { new { name = "weird", engine = "cobol", readOnly = false } };
        var result = await (await client.PostAsJsonAsync("/api/connections/import", payload, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Empty(result.GetProperty("imported").EnumerateArray());
        Assert.Contains("cobol", result.GetProperty("skipped")[0].GetProperty("reason").GetString());
    }

    private static async Task<Dictionary<string, string>> Ids(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
    }
}

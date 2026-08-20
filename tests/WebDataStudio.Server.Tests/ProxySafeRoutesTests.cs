using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WebDataStudio.Server.Tests;

/// An object reference contains a slash — "Table:dbo/AbpUsers" — and a reverse proxy decodes %2F
/// back into a real slash before the request reaches the app. Envoy does it in front of Azure
/// Container Apps, which is why a deployed studio answered 404 on every object lookup while the
/// same build was fine on a machine with nothing in front of it. References therefore travel in
/// the query string, where a slash is just a character.
public class ProxySafeRoutesTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-proxy").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people (id, name) VALUES (1, 'ada');
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
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public void No_route_carries_an_object_reference_in_its_path()
    {
        using var factory = Factory();
        // Resolving a service builds the app, which is what registers the endpoints.
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        var offenders = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(pattern => pattern.Contains("{objectRef}", StringComparison.OrdinalIgnoreCase)
                              || pattern.Contains("{ref}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "a proxy decodes %2F into a slash, so these routes cannot survive a deployment: "
            + string.Join(", ", offenders));
    }

    [Theory]
    // The shape a proxy leaves behind: the slash and the colon arrive as themselves.
    [InlineData("Table:main/people")]
    // And the shape a browser sends.
    [InlineData("Table%3Amain%2Fpeople")]
    public async Task An_object_can_be_described_however_the_reference_is_spelled(string reference)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync($"/api/schema/{id}/object?ref={reference}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains(body.GetProperty("columns").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "name");
    }

    [Fact]
    public async Task So_can_its_rows_and_its_ddl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var rows = await client.GetAsync($"/api/data/{id}?ref=Table:main/people", ct);
        var ddl = await client.GetAsync($"/api/ddl/{id}?ref=Table:main/people", ct);

        Assert.Equal(HttpStatusCode.OK, rows.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ddl.StatusCode);
    }
}

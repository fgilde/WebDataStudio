using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class ConnectionEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-api").FullName;

    public void Dispose()
    {
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] env) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                env.Append((Key: "DB_PATH", Value: Path.Combine(_dir, "wds.db")))
                   .Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))));

    public record Dto(string Id, string Name, string Engine, bool ReadOnly,
        string? Color, string? Group, string Source, string Summary);

    [Fact]
    public async Task Lists_environment_connections()
    {
        using var factory = Factory(("WDS_CONN_PROD", "postgres://app:pw@db:5432/shop"));
        var list = await factory.CreateClient()
            .GetFromJsonAsync<List<Dto>>("/api/connections", TestContext.Current.CancellationToken);

        var dto = Assert.Single(list!);
        Assert.Equal("PROD", dto.Name);
        Assert.Equal("Environment", dto.Source);
    }

    [Fact]
    public async Task Never_returns_the_connection_string()
    {
        using var factory = Factory(("WDS_CONN_PROD", "postgres://app:pw@db:5432/shop"));
        var raw = await factory.CreateClient()
            .GetStringAsync("/api/connections", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("pw", raw);
        Assert.DoesNotContain("Password", raw);
    }

    [Fact]
    public async Task Creates_updates_and_deletes_a_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/connections", new
        {
            name = "local-pg",
            engine = "postgresql",
            connectionString = "Host=localhost;Database=shop;Username=app;Password=pw",
            readOnly = false,
        }, ct)).Content.ReadFromJsonAsync<Dto>(ct);

        Assert.Equal("local-pg", created!.Name);
        Assert.Equal("Stored", created.Source);

        var updated = await client.PutAsJsonAsync($"/api/connections/{created.Id}", new
        {
            name = "renamed",
            engine = "postgresql",
            connectionString = "Host=localhost;Database=shop;Username=app;Password=pw",
            readOnly = true,
        }, ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var deleted = await client.DeleteAsync($"/api/connections/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<Dto>>("/api/connections", ct))!);
    }

    [Fact]
    public async Task Environment_connections_cannot_be_modified()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_CONN_PROD", "postgres://app:pw@db/shop"));
        var client = factory.CreateClient();
        var id = (await client.GetFromJsonAsync<List<Dto>>("/api/connections", ct))!.Single().Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/connections/{id}", ct)).StatusCode);
    }

    [Fact]
    public async Task Rejects_an_unknown_engine()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/connections", new
        {
            name = "weird", engine = "notadb", connectionString = "x", readOnly = false,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Force_readonly_marks_every_connection_readonly()
    {
        using var factory = Factory(("WDS_CONN_PROD", "postgres://app:pw@db/shop"), ("WDS_READONLY", "true"));
        var list = await factory.CreateClient()
            .GetFromJsonAsync<List<Dto>>("/api/connections", TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(list!).ReadOnly);
    }

    // --- is this server still there? ---------------------------------------------------------------

    [Fact]
    public async Task Health_measures_one_round_trip_to_a_server_that_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = Path.Combine(_dir, "health.db");

        using var factory = Factory(("WDS_CONN_LOCAL", $"sqlite:///{db.Replace('\\', '/')}"));
        var client = factory.CreateClient();

        var list = await client.GetFromJsonAsync<List<Dto>>("/api/connections", ct);
        var health = await client.GetFromJsonAsync<JsonElement>(
            $"/api/connections/{list![0].Id}/health", ct);

        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.True(health.GetProperty("milliseconds").GetInt32() >= 0);
    }

    [Fact]
    public async Task A_server_that_is_down_is_an_answer_not_a_fault()
    {
        var ct = TestContext.Current.CancellationToken;

        // Nothing is listening there, and the studio itself is fine: 200 with ok=false, so the
        // explorer's dot has something to say rather than an exception to swallow.
        using var factory = Factory(("WDS_CONN_GONE", "postgres://app:pw@127.0.0.1:1/shop"));
        var client = factory.CreateClient();

        var list = await client.GetFromJsonAsync<List<Dto>>("/api/connections", ct);
        var response = await client.GetAsync($"/api/connections/{list![0].Id}/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(health.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(health.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Health_of_a_connection_that_does_not_exist_is_a_404()
    {
        using var factory = Factory();

        var response = await factory.CreateClient()
            .GetAsync("/api/connections/nope/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

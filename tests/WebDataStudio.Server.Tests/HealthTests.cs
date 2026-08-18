using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebDataStudio.Server.Tests;

public class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(ct);
        Assert.Equal("ok", body!.Status);
    }

    private record HealthResponse(string Status, string Version);
}

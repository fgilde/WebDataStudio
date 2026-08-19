using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class AuthOptionsTests
{
    [Fact]
    public void Anonymous_when_no_credentials_configured()
    {
        var options = AuthOptions.FromEnvironment(new Dictionary<string, string?>());
        Assert.True(options.Anonymous);
    }

    [Fact]
    public void Anonymous_when_only_user_configured()
    {
        var options = AuthOptions.FromEnvironment(new Dictionary<string, string?> { ["WDS_USER"] = "admin" });
        Assert.True(options.Anonymous);
    }

    [Fact]
    public void Requires_login_when_both_configured()
    {
        var options = AuthOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["WDS_USER"] = "admin",
            ["WDS_PASSWORD"] = "s3cret",
        });
        Assert.False(options.Anonymous);
        Assert.Equal("admin", options.Username);
    }
}

public class AuthEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-auth").FullName;

    public void Dispose()
    {
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] env) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                env.Append((Key: "DB_PATH", Value: Path.Combine(_dir, "wds.db")))
                   .Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))));

    [Fact]
    public async Task Me_reports_anonymous_when_unconfigured()
    {
        using var factory = Factory();
        var body = await factory.CreateClient()
            .GetFromJsonAsync<MeResponse>("/api/auth/me", TestContext.Current.CancellationToken);
        Assert.True(body!.Anonymous);
    }

    [Fact]
    public async Task Protected_endpoint_is_open_when_anonymous()
    {
        using var factory = Factory();
        var response = await factory.CreateClient()
            .GetAsync("/api/connections", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_login()
    {
        using var factory = Factory(("WDS_USER", "admin"), ("WDS_PASSWORD", "s3cret"));
        var response = await factory.CreateClient()
            .GetAsync("/api/connections", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_correct_credentials_grants_access()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_USER", "admin"), ("WDS_PASSWORD", "s3cret"));
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "s3cret" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var response = await client.GetAsync("/api/connections", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        using var factory = Factory(("WDS_USER", "admin"), ("WDS_PASSWORD", "s3cret"));
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "wrong" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private record MeResponse(bool Anonymous, bool Authenticated, string? Username, string? Title);

    [Fact]
    public async Task Me_carries_the_studio_title_when_one_is_set()
    {
        using var factory = Factory(("WDS_TITLE", "analytics studio"));
        var body = await factory.CreateClient()
            .GetFromJsonAsync<MeResponse>("/api/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal("analytics studio", body!.Title);
    }

    [Fact]
    public async Task Me_has_no_title_when_nothing_named_the_studio()
    {
        using var factory = Factory();
        var body = await factory.CreateClient()
            .GetFromJsonAsync<MeResponse>("/api/auth/me", TestContext.Current.CancellationToken);

        // Null, not an empty string: the header and the browser tab show nothing at all then.
        Assert.Null(body!.Title);
    }

    [Fact]
    public async Task A_blank_title_counts_as_no_title()
    {
        using var factory = Factory(("WDS_TITLE", "   "));
        var body = await factory.CreateClient()
            .GetFromJsonAsync<MeResponse>("/api/auth/me", TestContext.Current.CancellationToken);

        Assert.Null(body!.Title);
    }

    [Fact]
    public async Task The_title_is_readable_before_signing_in()
    {
        // The login screen shows it, so it cannot sit behind the login.
        using var factory = Factory(("WDS_USER", "admin"), ("WDS_PASSWORD", "s3cret"),
            ("WDS_TITLE", "production"));

        var body = await factory.CreateClient()
            .GetFromJsonAsync<MeResponse>("/api/auth/me", TestContext.Current.CancellationToken);

        Assert.False(body!.Authenticated);
        Assert.Equal("production", body.Title);
    }
}

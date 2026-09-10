using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A deployment's own icon, from `WDS_ICON`.
///
/// Two shapes have to work, because both are what people actually have: a URL somewhere else, and a
/// file mounted into the container. A browser cannot read the second one, so the studio serves it —
/// and the login screen has to reach it before anybody signed in.
public class StudioIconTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-icon").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<JsonElement> MeAsync(WebApplicationFactory<Program> factory)
    {
        using var body = JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/api/auth/me", Ct));

        return body.RootElement.Clone();
    }

    private string Icon(string name, string content = "<svg/>")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_studio_nobody_gave_an_icon_says_nothing_about_one()
    {
        using var factory = Factory();

        var me = await MeAsync(factory);

        // Null rather than our own path: the default belongs to the client, which ships the file.
        Assert.True(me.GetProperty("icon").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task A_url_is_handed_to_the_browser_as_it_was_given()
    {
        using var factory = Factory(("WDS_ICON", "https://example.com/logo.png"));

        var me = await MeAsync(factory);

        Assert.Equal("https://example.com/logo.png", me.GetProperty("icon").GetString());
    }

    [Fact]
    public async Task A_mounted_file_is_served_by_the_studio_itself()
    {
        using var factory = Factory(("WDS_ICON", Icon("brand.svg", "<svg id='mine'/>")));

        var me = await MeAsync(factory);
        Assert.Equal("/api/brand/icon", me.GetProperty("icon").GetString());

        var answer = await factory.CreateClient().GetAsync("/api/brand/icon", Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("image/svg+xml", answer.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id='mine'", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_type_comes_from_the_extension()
    {
        using var factory = Factory(("WDS_ICON", Icon("brand.png")));

        var answer = await factory.CreateClient().GetAsync("/api/brand/icon", Ct);

        Assert.Equal("image/png", answer.Content.Headers.ContentType?.MediaType);
    }

    /// The login screen shows the icon, and nobody is signed in there yet.
    [Fact]
    public async Task The_icon_is_readable_before_signing_in()
    {
        using var factory = Factory(
            ("WDS_USERS", $"boss:admin:{UserStore.Hash("hunter2")}"),
            ("WDS_ICON", Icon("brand.svg")));

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/connections", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/brand/icon", Ct)).StatusCode);
    }

    [Fact]
    public async Task Asking_for_an_icon_nobody_configured_is_a_plain_404()
    {
        using var factory = Factory();

        var answer = await factory.CreateClient().GetAsync("/api/brand/icon", Ct);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    /// A path that is not there is a typo in a deployment, not a reason to refuse to start — and not
    /// a reason to hand the browser a path it cannot fetch either. It travels as given and the
    /// browser falls back to the shipped icon.
    [Fact]
    public async Task A_path_that_is_not_there_does_not_stop_the_studio()
    {
        using var factory = Factory(("WDS_ICON", Path.Combine(_dir, "not-here.svg")));

        var me = await MeAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await factory.CreateClient().GetAsync("/api/brand/icon", Ct)).StatusCode);
        Assert.NotEqual("/api/brand/icon", me.GetProperty("icon").GetString());
    }
}

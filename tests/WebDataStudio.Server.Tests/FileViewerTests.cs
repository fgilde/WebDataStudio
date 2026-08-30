using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Endpoints;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The rich file viewer is fetched from somewhere else the first time somebody looks at a file, so
/// where it comes from is a deployment's decision — including "nowhere".
public class FileViewerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-viewer").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory(string? url) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_FILE_VIEWER_URL"] = url,
            })));

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WDS_FILE_VIEWER_URL"] = value }).Build();

    [Fact]
    public void Unset_means_the_public_one()
    {
        var options = FileViewerOptions.FromConfiguration(Config(null));

        Assert.True(options.Enabled);
        Assert.Equal(FileViewerOptions.Default, options.ScriptUrl);
    }

    [Fact]
    public void A_deployment_can_point_it_at_its_own_copy()
    {
        var options = FileViewerOptions.FromConfiguration(Config("  https://intranet/mudex.js  "));

        Assert.True(options.Enabled);
        Assert.Equal("https://intranet/mudex.js", options.ScriptUrl);
    }

    [Fact]
    public void Set_to_nothing_means_somebody_said_no_on_purpose()
    {
        // A studio with no way out to the internet, told to stop reaching for one.
        var options = FileViewerOptions.FromConfiguration(Config(""));

        Assert.False(options.Enabled);
        Assert.Null(options.ScriptUrl);
    }

    [Fact]
    public async Task Health_says_where_it_is()
    {
        using var factory = Factory("https://intranet/mudex.js");

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", Ct);

        Assert.Equal("https://intranet/mudex.js",
            body.GetProperty("fileViewer").GetProperty("script").GetString());
    }

    // --- the page the viewer runs on ---------------------------------------------------------------

    [Fact]
    public async Task The_frame_is_a_page_of_its_own_that_loads_the_viewer()
    {
        using var factory = Factory(null);

        var page = await factory.CreateClient().GetStringAsync(
            "/api/viewer/frame?url=%2Fapi%2Fstorage%2Fc1%2Fdownload%3Fref%3Dx&name=q.xlsx"
            + "&type=application%2Fvnd.ms-excel&dark=true", Ct);

        Assert.Contains(FileViewerOptions.Default, page);
        Assert.Contains("mudex-file-display", page);
        Assert.Contains("/api/storage/c1/download?ref=x", page);
        Assert.Contains("\"dark\": true", page.Replace(" ", "").Replace("\"dark\":true", "\"dark\": true"));

        // As an attribute: this element has a `style` property of its own, a string, and setting
        // `style.display` on it throws — which is what turned the studio's page grey.
        Assert.Contains("setAttribute(\"style\"", page);
        Assert.DoesNotContain("element.style.", page);

        // The two the component needs said as attributes, the way its own documentation shows.
        Assert.Contains("\"dense\", \"true\"", page);
        Assert.Contains("\"show-file-name\", \"false\"", page);
    }

    [Fact]
    public async Task The_frame_opens_only_what_this_studio_serves()
    {
        using var factory = Factory(null);
        var client = factory.CreateClient();

        // A link to this page must not become a way to frame somebody else's site, or to have the
        // viewer fetch an address a visitor picked.
        foreach (var elsewhere in new[]
        {
            "https://example.org/x.pdf", "//example.org/x.pdf", "http://localhost:9/x", "",
        })
        {
            var response = await client.GetAsync(
                $"/api/viewer/frame?url={Uri.EscapeDataString(elsewhere)}&name=x", Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public void What_counts_as_ours()
    {
        Assert.True(ViewerEndpoints.IsOurs("/api/storage/c1/download?ref=x"));
        // A file the browser made for itself, out of a cell's bytes.
        Assert.True(ViewerEndpoints.IsOurs("blob:http://localhost:8080/2b6f0"));

        Assert.False(ViewerEndpoints.IsOurs("//example.org/x"));
        Assert.False(ViewerEndpoints.IsOurs("https://example.org/x"));
        Assert.False(ViewerEndpoints.IsOurs("javascript:alert(1)"));
        Assert.False(ViewerEndpoints.IsOurs(""));
    }

    [Fact]
    public async Task A_studio_without_a_viewer_has_no_page_either()
    {
        using var factory = Factory("");

        var response = await factory.CreateClient().GetAsync(
            "/api/viewer/frame?url=%2Fapi%2Fstorage%2Fx&name=a.csv", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_says_null_for_a_studio_without_one()
    {
        using var factory = Factory("");

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", Ct);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("fileViewer").ValueKind);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task Health_says_null_for_a_studio_without_one()
    {
        using var factory = Factory("");

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", Ct);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("fileViewer").ValueKind);
    }
}

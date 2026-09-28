using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The picker for files the container already has, and the fence around it.
public class FileBrowseEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-browse").FullName;
    private readonly string _mounted;
    private readonly string _outside;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileBrowseEndpointTests()
    {
        _mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;

        File.WriteAllText(Path.Combine(_mounted, "shop.sqlite3"), "SQLite format 3\0");
        File.WriteAllText(Path.Combine(_mounted, "people.csv"), "id,name\n1,ada\n");
        File.WriteAllText(Path.Combine(_mounted, "notes.zip"), "x");
        Directory.CreateDirectory(Path.Combine(_mounted, "reports"));
        File.WriteAllText(Path.Combine(_outside, "secret.db"), "x");
    }

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
                    ["WDS_FILE_ROOTS"] = _mounted,
                };
                foreach (var (key, value) in extra) settings[key] = value;
                c.AddInMemoryCollection(settings);
            }));

    private static Task<HttpResponseMessage> MakeFolderAsync(HttpClient client, string path, string name) =>
        client.PostAsJsonAsync("/api/connections/browse/folder", new { path, name }, Ct);

    private async Task<JsonDocument> BrowseAsync(HttpClient client, string? path = null) =>
        JsonDocument.Parse(await client.GetStringAsync(
            path is null ? "/api/connections/browse" : $"/api/connections/browse?path={Uri.EscapeDataString(path)}",
            Ct));

    [Fact]
    public async Task A_root_lists_its_folders_and_says_which_engine_opens_each_file()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var page = await BrowseAsync(client, _mounted);

        var directories = page.RootElement.GetProperty("directories").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString()).ToList();
        var files = page.RootElement.GetProperty("files").EnumerateArray().ToList();

        Assert.Contains("reports", directories);

        Assert.Contains(files, f => f.GetProperty("name").GetString() == "shop.sqlite3"
                                    && f.GetProperty("engine").GetString() == "sqlite");
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "people.csv"
                                    && f.GetProperty("engine").GetString() == "storage");

        // The zip is listed with no engine rather than hidden: seeing it and being told nothing
        // opens it beats wondering where the file went.
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "notes.zip"
                                    && f.GetProperty("engine").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_file_says_how_big_it_is()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var page = await BrowseAsync(client, _mounted);

        var csv = page.RootElement.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "people.csv");

        Assert.Equal(new FileInfo(Path.Combine(_mounted, "people.csv")).Length,
            csv.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Without_a_path_it_answers_with_the_roots()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var page = await BrowseAsync(client);

        var roots = page.RootElement.GetProperty("roots").EnumerateArray()
            .Select(r => r.GetString()).ToList();

        Assert.Contains(_mounted, roots);
        Assert.Contains(Path.Combine(_dir, "data"), roots);
        Assert.Null(page.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Inside_a_root_the_parent_is_offered_and_at_the_top_it_is_not()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var top = await BrowseAsync(client, _mounted);
        Assert.Null(top.RootElement.GetProperty("parent").GetString());

        using var deeper = await BrowseAsync(client, Path.Combine(_mounted, "reports"));
        Assert.Equal(_mounted, deeper.RootElement.GetProperty("parent").GetString());
    }

    [Fact]
    public async Task Outside_the_roots_it_refuses_rather_than_lists()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(_outside)}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("WDS_FILE_ROOTS", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Climbing_out_with_dot_dot_refuses_too()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var climb = Path.Combine(_mounted, "..", "outside");

        var response = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(climb)}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_reads_as_not_allowed_rather_than_as_a_crash()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(Path.Combine(_mounted, "never"))}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_new_folder_is_made_inside_the_folder_that_was_open()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await MakeFolderAsync(client, Path.Combine(_mounted, "reports"), "2026");

        response.EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(Path.Combine(_mounted, "reports", "2026")));
    }

    [Fact]
    public async Task A_folder_is_never_made_outside_the_roots_nor_with_a_path_for_a_name()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await MakeFolderAsync(client, _outside, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MakeFolderAsync(client, _mounted, "../outside/x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MakeFolderAsync(client, _mounted, "..")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_outside, "x")));
    }

    [Fact]
    public async Task A_folder_that_is_there_already_is_a_conflict()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await MakeFolderAsync(client, _mounted, "reports")).StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_makes_folders_on_the_server()
    {
        using var factory = Factory(("WDS_USERS",
            $"ada:admin:{Server.Services.UserStore.Hash("one")};bob:editor:{Server.Services.UserStore.Hash("two")}"));

        var editor = factory.CreateClient();
        (await editor.PostAsJsonAsync("/api/auth/login", new { username = "bob", password = "two" }, Ct))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await MakeFolderAsync(editor, _mounted, "bob")).StatusCode);

        var admin = factory.CreateClient();
        (await admin.PostAsJsonAsync("/api/auth/login", new { username = "ada", password = "one" }, Ct))
            .EnsureSuccessStatusCode();
        (await MakeFolderAsync(admin, _mounted, "ada")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_studio_that_closed_browsing_makes_no_folders_either()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_BROWSE", "false"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await MakeFolderAsync(client, _mounted, "x")).StatusCode);
    }
}

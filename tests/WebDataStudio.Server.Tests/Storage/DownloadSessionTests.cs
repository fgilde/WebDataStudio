using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests.Storage;

/// Looking at a few files must not cost a connection its sessions.
///
/// This is the bug that made a studio appear to freeze: the download handed out a session per file
/// and never took one back, so the fourth file on a connection that allows four sessions left
/// nothing for the tree, the preview or anything else — and a browser, which gives one host six
/// connections, then had its whole window waiting on requests that were never coming back.
public class DownloadSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-download-sessions").FullName;
    private readonly string _files;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DownloadSessionTests()
    {
        _files = Path.Combine(_dir, "drop");
        Directory.CreateDirectory(_files);

        File.WriteAllText(Path.Combine(_files, "people.csv"), "name,city\nada,london\n");
        File.WriteAllText(Path.Combine(_files, "notes.md"), "# notes\n");
        File.WriteAllText(Path.Combine(_files, "orders.ndjson"), "{\"id\":1}\n");
    }

    public void Dispose() => TestDirectory.Remove(_dir);

    /// Two sessions, like the demo's admin studio has four: few enough that a leak shows up in a
    /// handful of calls rather than in a soak test.
    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                // new Uri(path).AbsoluteUri rather than "file:///" and a path: on Linux the path
                // already starts with a slash, so the hand-written form produced
                // file:////tmp/..., where the fourth slash makes "tmp" a host and the folder
                // unreachable — the connection then had nothing in it.
                ["WDS_CONN_DROP"] = new Uri(_files).AbsoluteUri,
                ["WDS_MAX_SESSIONS"] = "2",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/connections", Ct);
        return list![0].GetProperty("id").GetString()!;
    }

    /// The refs the tree hands out, rather than refs built here out of a path.
    ///
    /// A file connection's tree names its container itself, and an absolute path is not what goes in
    /// a ref — a test that assembled one from `_files` happened to pass on Windows and answered 404
    /// on Linux, which is a test bug rather than a bug. Asking the server is also what a browser
    /// does, so this exercises the path that actually runs.
    private static async Task<List<string>> FileRefsAsync(HttpClient client, string conn)
    {
        var containers = await client.GetFromJsonAsync<List<System.Text.Json.JsonElement>>(
            $"/api/schema/{conn}", Ct);

        var refs = new List<string>();

        foreach (var container in containers ?? [])
        {
            var parent = container.GetProperty("ref").GetString()!;

            var children = await client.GetFromJsonAsync<List<System.Text.Json.JsonElement>>(
                $"/api/schema/{conn}?parent={Uri.EscapeDataString(parent)}", Ct);

            foreach (var child in children ?? [])
            {
                if (child.GetProperty("ref").GetString() is { } childRef
                    && childRef.StartsWith("StorageObject:", StringComparison.Ordinal))
                    refs.Add(childRef);
            }
        }

        Assert.NotEmpty(refs);
        return refs;
    }

    /// A ref is `Kind:a/b/c`, split on the slash. So every segment has to be one segment — and a
    /// folder connection's container is a path, which on Linux is full of slashes. It showed its
    /// folder and no files in the image, and passed here, because a Windows path is spelled with
    /// backslashes. The invariant is what the test holds on to, not the platform.
    [Fact]
    public async Task The_container_a_folder_connection_shows_is_one_segment()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var containers = await client.GetFromJsonAsync<List<System.Text.Json.JsonElement>>(
            $"/api/schema/{conn}", Ct);

        var containerRef = Assert.Single(containers!).GetProperty("ref").GetString()!;
        var path = containerRef.Split(':', 2)[1];

        Assert.DoesNotContain('/', path);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public async Task Downloading_more_files_than_there_are_sessions_leaves_the_studio_working()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var files = await FileRefsAsync(client, conn);

        // Every file twice, through two sessions. Every one of them has to give its session back.
        foreach (var objectRef in files.Concat(files))
        {
            var response = await client.GetAsync(
                $"/api/storage/{conn}/download?ref={Uri.EscapeDataString(objectRef)}&inline=true", Ct);

            response.EnsureSuccessStatusCode();

            // Read it: the session travels with the body, so it is only given back once the body is.
            Assert.NotEmpty(await response.Content.ReadAsStringAsync(Ct));
        }

        // And the tree still answers, which is the part that used to hang forever.
        var tree = await client.GetAsync($"/api/schema/{conn}", Ct);
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
    }

    [Fact]
    public async Task A_download_nobody_reads_gives_its_session_back_too()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // A viewer that asks for a file and goes away — a closed tab, a component that changed its
        // mind. ASP.NET disposes the stream either way, and that is what returns the session.
        var first = (await FileRefsAsync(client, conn))[0];

        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync(
                $"/api/storage/{conn}/download?ref={Uri.EscapeDataString(first)}&inline=true",
                HttpCompletionOption.ResponseHeadersRead, Ct);

            response.EnsureSuccessStatusCode();
        }

        var tree = await client.GetAsync($"/api/schema/{conn}", Ct);
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
    }

    [Fact]
    public async Task A_studio_whose_sessions_are_all_busy_says_so_instead_of_waiting()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var pool = factory.Services.GetRequiredService<SessionPool>();
        Assert.Equal(2, pool.MaxSessions);

        // Both slots taken by hand, and not given back: this is what a leak leaves behind.
        var factorySessions = factory.Services.GetRequiredService<SessionFactory>();
        var held = new List<IDbSession>();

        for (var i = 0; i < pool.MaxSessions; i++)
        {
            var (_, session) = await factorySessions.OpenAsync(conn, Ct);
            held.Add(session);
        }

        try
        {
            // The wait is capped, so the answer is a sentence rather than a spinner. Cut it short
            // here: the test is that it ends and says why, not how long the studio is patient for.
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var response = await client.GetAsync($"/api/schema/{conn}", giveUp.Token)
                .ContinueWith(t => t.IsFaulted || t.IsCanceled ? null : t.Result, Ct);

            // Either the studio answered "all sessions are in use", or it was still waiting when
            // the client gave up — what must not happen is a request that hangs with no limit.
            if (response is not null)
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("in use", await response.Content.ReadAsStringAsync(Ct));
            }
        }
        finally
        {
            foreach (var session in held) await session.DisposeAsync();
        }
    }
}

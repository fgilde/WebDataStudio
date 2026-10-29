using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A connection that came out of a link belongs to the browser that opened it: nobody else sees it,
/// and nothing about it is written down.
public class SessionConnectionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-session-conns").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private static ConnectionSpec Spec(string id, string name) =>
        new(id, name, "sqlite", "Data Source=:memory:", true, null, null, ConnectionSource.Session);

    [Fact]
    public async Task Every_client_gets_a_session_cookie()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/connections", Ct);

        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("wds_session=", StringComparison.Ordinal)
                      && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_session_connection_is_seen_only_under_its_own_key()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        sessions.Add("cookie-a", Spec("s1", "SHOP"));

        Assert.Single(sessions.For("cookie-a"));
        Assert.Empty(sessions.For("cookie-b"));
    }

    [Fact]
    public void Adding_the_same_id_twice_is_one_connection()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        sessions.Add("cookie-a", Spec("same", "SHOP"));
        sessions.Add("cookie-a", Spec("same", "SHOP again"));

        var only = Assert.Single(sessions.For("cookie-a"));
        Assert.Equal("SHOP again", only.Name);
    }

    [Fact]
    public void What_it_keeps_is_marked_as_a_session_connection()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        var kept = sessions.Add("cookie-a",
            Spec("s1", "SHOP") with { Source = ConnectionSource.Stored });

        Assert.Equal(ConnectionSource.Session, kept.Source);
    }

    /// The point of the whole thing: two people on one studio do not see each other's links.
    [Fact]
    public async Task Two_clients_do_not_see_each_others_session_connections()
    {
        using var factory = Factory();
        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        // Asking once is how each client gets its cookie; the server then knows both keys.
        await mine.GetAsync("/api/connections", Ct);
        await theirs.GetAsync("/api/connections", Ct);

        var keys = sessions.Keys.ToList();
        Assert.Equal(2, keys.Count);

        sessions.Add(keys[0], Spec("only-one-sees-this", "MINE"));

        async Task<int> CountAsync(HttpClient client)
        {
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
            return list.RootElement.GetArrayLength();
        }

        // One of them sees it and the other does not; which one depends on whose cookie came first.
        var counts = new[] { await CountAsync(mine), await CountAsync(theirs) };

        Assert.Contains(1, counts);
        Assert.Contains(0, counts);
    }

    [Fact]
    public async Task It_is_in_the_list_as_a_session_connection()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/connections", Ct);

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        sessions.Add(sessions.Keys.First(), Spec("from-link", "LINK"));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        var connection = list.RootElement.EnumerateArray().Single();

        Assert.Equal("LINK", connection.GetProperty("name").GetString());
        Assert.Equal("Session", connection.GetProperty("source").GetString());
    }

    /// It is this browser's connection, so this browser may drop it. Nothing is stored, so there is
    /// nothing left behind once it is gone.
    [Fact]
    public async Task The_browser_it_belongs_to_may_delete_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/connections", Ct);

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        sessions.Add(sessions.Keys.First(), Spec("from-link", "LINK"));

        var deleted = await client.DeleteAsync("/api/connections/from-link", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(sessions.For(sessions.Keys.First()));
    }

    /// The edit form never gets a tunnel back, so an edit that sends none keeps the one there is
    /// rather than quietly dropping it.
    [Fact]
    public async Task Editing_it_keeps_its_tunnel()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/connections", Ct);

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var key = sessions.Keys.First();
        sessions.Add(key, Spec("from-link", "LINK") with { Tunnel = new TunnelSpec("bastion", 22, "me") });

        var edited = await client.PutAsJsonAsync("/api/connections/from-link", new
        {
            name = "RENAMED", engine = "sqlite", connectionString = "Data Source=:memory:", readOnly = true,
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var connection = sessions.For(key).Single();
        Assert.Equal("RENAMED", connection.Name);
        Assert.Equal("bastion", connection.Tunnel?.Host);
    }

    /// And nobody else's browser may: it does not exist for them, which is a 404 rather than a
    /// refusal that would confirm it is there.
    [Fact]
    public async Task Another_browser_cannot_touch_it()
    {
        using var factory = Factory();
        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        await mine.GetAsync("/api/connections", Ct);
        await theirs.GetAsync("/api/connections", Ct);

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var keys = sessions.Keys.ToList();
        sessions.Add(keys[0], Spec("from-link", "LINK"));

        // Whichever client owns the key, exactly one of them may delete it and the other gets a 404.
        var answers = new[]
        {
            (await mine.DeleteAsync("/api/connections/from-link", Ct)).StatusCode,
            (await theirs.DeleteAsync("/api/connections/from-link", Ct)).StatusCode,
        };

        Assert.Contains(HttpStatusCode.NoContent, answers);
        Assert.Contains(HttpStatusCode.NotFound, answers);
    }

    /// A session connection is a connection: the studio opens it like any other, which is what makes
    /// the whole feature worth having.
    [Fact]
    public async Task The_studio_can_open_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/connections", Ct);

        var file = Path.Combine(_dir, "session.db");

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT);";
            command.ExecuteNonQuery();
        }

        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        sessions.Add(sessions.Keys.First(), new ConnectionSpec("live", "LIVE", "sqlite",
            $"Data Source={file}", true, null, null, ConnectionSource.Session));

        using var tree = JsonDocument.Parse(await client.GetStringAsync("/api/schema/live", Ct));

        Assert.NotEmpty(tree.RootElement.EnumerateArray());
    }
}

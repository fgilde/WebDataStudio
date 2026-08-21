using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The account list itself: parsing, roles, and that verification does not leak by timing shape.
public class UserStoreTests
{
    [Fact]
    public void No_accounts_means_the_studio_runs_open()
    {
        Assert.True(new UserStore([]).Anonymous);
    }

    [Fact]
    public void An_entry_carries_a_role_and_its_connections()
    {
        var users = UserStore.Parse("ada:admin:secret;grace:viewer:other:prod,staging");

        Assert.Equal(2, users.Count);
        Assert.Equal(UserRoles.Admin, users[0].Role);
        Assert.Empty(users[0].Connections);
        Assert.Equal(UserRoles.Viewer, users[1].Role);
        Assert.Equal(["prod", "staging"], users[1].Connections.OrderBy(c => c));
    }

    /// A typo in the role must not silently grant more than intended, so anything unknown is the
    /// least powerful role rather than the most.
    [Fact]
    public void An_unknown_role_is_a_viewer()
    {
        Assert.Equal(UserRoles.Viewer, UserStore.Parse("ada:wizard:secret")[0].Role);
    }

    [Fact]
    public void An_entry_without_a_secret_is_not_an_account()
    {
        Assert.Empty(UserStore.Parse("ada:admin:;:admin:secret;nonsense"));
    }

    [Fact]
    public void A_hashed_password_verifies_and_a_wrong_one_does_not()
    {
        var hash = UserStore.Hash("correct horse");

        Assert.True(UserStore.VerifySecret(hash, "correct horse"));
        Assert.False(UserStore.VerifySecret(hash, "correct horse "));
        Assert.False(UserStore.VerifySecret(hash, ""));
    }

    [Fact]
    public void A_damaged_hash_verifies_nothing()
    {
        Assert.False(UserStore.VerifySecret("pbkdf2$210000$not-base64$neither", "anything"));
        Assert.False(UserStore.VerifySecret("pbkdf2$notanumber$AAAA$AAAA", "anything"));
    }

    [Fact]
    public void Verification_finds_the_right_account()
    {
        var store = new UserStore(UserStore.Parse(
            $"ada:admin:{UserStore.Hash("one")};grace:editor:{UserStore.Hash("two")}"));

        Assert.Equal(UserRoles.Editor, store.Verify("grace", "two")?.Role);
        Assert.Null(store.Verify("grace", "one"));
        Assert.Null(store.Verify("nobody", "two"));
    }

    /// The single-account variables predate the list and still have to work.
    [Fact]
    public void The_old_single_account_variables_are_an_admin()
    {
        var store = UserStore.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WDS_USER"] = "root", ["WDS_PASSWORD"] = "s3cret",
            }).Build());

        Assert.False(store.Anonymous);
        Assert.Equal(UserRoles.Admin, store.Verify("root", "s3cret")?.Role);
        Assert.Null(store.Verify("root", "wrong"));
    }

    [Fact]
    public void A_whitelist_hides_everything_it_does_not_name()
    {
        var user = UserStore.Parse("grace:viewer:x:prod")[0];

        Assert.True(user.MaySee("env-123", "prod"));
        Assert.False(user.MaySee("env-456", "staging"));
        // No list at all means every connection.
        Assert.True(UserStore.Parse("ada:admin:x")[0].MaySee("env-456", "staging"));
    }
}

/// The same rules through the API: what a role can reach, and what it cannot.
public class StudioUsersTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-users").FullName;

    public async ValueTask InitializeAsync()
    {
        foreach (var name in new[] { "prod", "staging" })
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, name + ".db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1, 'a');";
            await command.ExecuteNonQueryAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private string Path_(string name) =>
        System.IO.Path.Combine(_dir, name + ".db").Replace(System.IO.Path.DirectorySeparatorChar, '/');

    private WebApplicationFactory<Program> Factory(string users) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = System.IO.Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PROD"] = "sqlite:///" + Path_("prod"),
                ["WDS_CONN_STAGING"] = "sqlite:///" + Path_("staging"),
                ["WDS_USERS"] = users,
            })));

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory,
        string name, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = name, password }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<List<JsonElement>> ConnectionsAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/connections",
            TestContext.Current.CancellationToken);

        return [.. body.EnumerateArray()];
    }

    [Fact]
    public async Task A_viewer_sees_only_their_connections_and_all_of_them_read_only()
    {
        using var factory = Factory(
            $"ada:admin:{UserStore.Hash("one")};grace:viewer:{UserStore.Hash("two")}:PROD");
        var viewer = await SignedInAsync(factory, "grace", "two");

        var connections = await ConnectionsAsync(viewer);

        var only = Assert.Single(connections);
        Assert.Equal("PROD", only.GetProperty("name").GetString());
        Assert.True(only.GetProperty("readOnly").GetBoolean());
    }

    /// Not listed means not there: a connection somebody may not see cannot be reached by guessing
    /// its id either.
    [Fact]
    public async Task A_connection_outside_the_whitelist_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            $"ada:admin:{UserStore.Hash("one")};grace:viewer:{UserStore.Hash("two")}:PROD");

        var admin = await SignedInAsync(factory, "ada", "one");
        var staging = (await ConnectionsAsync(admin))
            .First(c => c.GetProperty("name").GetString() == "STAGING")
            .GetProperty("id").GetString();

        var viewer = await SignedInAsync(factory, "grace", "two");
        var response = await viewer.GetAsync($"/api/data/{staging}?ref=Table:main/t", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_sees_everything()
    {
        using var factory = Factory($"ada:admin:{UserStore.Hash("one")}");
        var admin = await SignedInAsync(factory, "ada", "one");

        var connections = await ConnectionsAsync(admin);

        Assert.Equal(2, connections.Count);
        Assert.All(connections, c => Assert.False(c.GetProperty("readOnly").GetBoolean()));
    }

    [Fact]
    public async Task An_editor_may_write_but_not_administer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"eve:editor:{UserStore.Hash("three")}");
        var editor = await SignedInAsync(factory, "eve", "three");

        var id = (await ConnectionsAsync(editor)).First().GetProperty("id").GetString();

        // Writing is allowed: the change is previewed rather than refused.
        var preview = await editor.PostAsJsonAsync($"/api/data/{id}/preview-changes?ref=Table:main/t",
            new { changes = new[] { new { kind = "update", key = new { id = 1 }, values = new { v = "b" } } } }, ct);

        Assert.True(preview.IsSuccessStatusCode);

        // The administration surface is not.
        var admin = await editor.GetAsync($"/api/admin/sessions/{id}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_write_even_where_the_connection_allows_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"grace:viewer:{UserStore.Hash("two")}");
        var viewer = await SignedInAsync(factory, "grace", "two");

        var id = (await ConnectionsAsync(viewer)).First().GetProperty("id").GetString();

        var preview = await viewer.PostAsJsonAsync($"/api/data/{id}/preview-changes?ref=Table:main/t",
            new { changes = new[] { new { kind = "update", key = new { id = 1 }, values = new { v = "b" } } } }, ct);
        var hash = (await preview.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("hash").GetString();

        var applied = await viewer.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref=Table:main/t",
            new { hash }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, applied.StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_role_so_the_UI_can_stop_offering_what_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"grace:viewer:{UserStore.Hash("two")}");
        var viewer = await SignedInAsync(factory, "grace", "two");

        var me = await viewer.GetFromJsonAsync<JsonElement>("/api/auth/me", ct);

        Assert.Equal("viewer", me.GetProperty("role").GetString());
        Assert.Equal("grace", me.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Wrong_credentials_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"grace:viewer:{UserStore.Hash("two")}");

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "grace", password = "one" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

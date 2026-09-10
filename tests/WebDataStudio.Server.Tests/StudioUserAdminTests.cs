using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Accounts an admin can see, make, change and remove.
///
/// Until now they came from `WDS_USERS` and nowhere else: the panel could list them and hash a
/// password, and everything else was a container rollout. A studio that runs for a year needs a
/// person added on a Tuesday, so accounts get a store — with the environment's own accounts staying
/// read-only, the way an environment connection does.
public class StudioUserAdminTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-studio-users").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    /// A studio with one admin from the environment, which is the usual starting point.
    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            ["WDS_USERS"] = $"boss:admin:{UserStore.Hash("hunter2")}",
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory,
        string user = "boss", string password = "hunter2")
    {
        var client = factory.CreateClient();
        var answer = await client.PostAsJsonAsync("/api/auth/login",
            new { username = user, password }, Ct);

        answer.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/admin/studio-users", Ct));

        return body.RootElement.GetProperty("users").EnumerateArray()
            .Select(u => u.Clone()).ToList();
    }

    private static object Account(string name, string password, string role = "editor",
        string[]? connections = null) =>
        new { name, password, role, connections = connections ?? [] };

    // --- the list ---------------------------------------------------------------------------------

    /// Both kinds side by side, each saying which it is — the panel needs that to know what it may
    /// offer a pencil for.
    [Fact]
    public async Task The_list_says_where_each_account_comes_from()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        var users = await ListAsync(client);

        Assert.Equal("Environment", users.Single(u => u.GetProperty("name").GetString() == "boss")
            .GetProperty("source").GetString());
        Assert.Equal("Stored", users.Single(u => u.GetProperty("name").GetString() == "clara")
            .GetProperty("source").GetString());
    }

    /// A password never comes back, not even hashed: the list is for a person, and a hash in a
    /// browser's memory is a hash somebody can take away.
    [Fact]
    public async Task The_list_carries_no_secret()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        var text = await client.GetStringAsync("/api/admin/studio-users", Ct);

        Assert.DoesNotContain("pbkdf2", text);
        Assert.DoesNotContain("hunter2", text);
    }

    // --- making one -------------------------------------------------------------------------------

    [Fact]
    public async Task An_account_that_was_made_can_sign_in()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("clara", "s3cret", "editor"), Ct)).EnsureSuccessStatusCode();

        using var fresh = factory.CreateClient();
        var login = await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = "clara", password = "s3cret" }, Ct);

        login.EnsureSuccessStatusCode();

        using var me = JsonDocument.Parse(await fresh.GetStringAsync("/api/auth/me", Ct));
        Assert.Equal("editor", me.RootElement.GetProperty("role").GetString());
    }

    /// The password is kept as a hash, whatever arrives. The environment still allows a literal for
    /// the single-account pair; a store never writes one.
    [Fact]
    public async Task What_is_stored_is_a_hash_and_not_the_password()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        var stored = factory.Services.GetRequiredService<StudioUserStore>()
            .List().Single(u => u.Name == "clara");

        Assert.StartsWith("pbkdf2$", stored.Secret);
        Assert.DoesNotContain("s3cret", stored.Secret);
    }

    [Fact]
    public async Task A_name_that_is_taken_is_refused()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        // The same name in another spelling is the same name: a login is case-insensitive.
        var again = await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("CLARA", "other"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_name_the_environment_already_uses_is_refused()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        var answer = await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("boss", "whatever", "admin"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains("WDS_USERS", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_account_needs_a_name_and_a_password()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/admin/studio-users", Account("  ", "s3cret"), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", ""), Ct)).StatusCode);
    }

    // --- changing one -----------------------------------------------------------------------------

    [Fact]
    public async Task A_role_and_the_connections_can_be_changed()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        var changed = await client.PutAsJsonAsync("/api/admin/studio-users/clara",
            new { role = "viewer", connections = new[] { "SHOP" } }, Ct);

        changed.EnsureSuccessStatusCode();

        var clara = (await ListAsync(client)).Single(u => u.GetProperty("name").GetString() == "clara");

        Assert.Equal("viewer", clara.GetProperty("role").GetString());
        Assert.Equal("SHOP", clara.GetProperty("connections").EnumerateArray().Single().GetString());
    }

    /// No password in the body means the one they have keeps working — an admin fixing a role should
    /// not have to know somebody's password.
    [Fact]
    public async Task Leaving_the_password_out_keeps_it()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/admin/studio-users/clara",
            new { role = "viewer", connections = Array.Empty<string>() }, Ct)).EnsureSuccessStatusCode();

        using var fresh = factory.CreateClient();
        var login = await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = "clara", password = "s3cret" }, Ct);

        login.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_password_in_the_body_replaces_the_old_one()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/admin/studio-users/clara",
            new { role = "editor", connections = Array.Empty<string>(), password = "n3wer" }, Ct))
            .EnsureSuccessStatusCode();

        using var fresh = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = "clara", password = "s3cret" }, Ct)).StatusCode);

        (await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = "clara", password = "n3wer" }, Ct)).EnsureSuccessStatusCode();
    }

    // --- removing one -----------------------------------------------------------------------------

    [Fact]
    public async Task An_account_can_be_removed_and_then_cannot_sign_in()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users", Account("clara", "s3cret"), Ct))
            .EnsureSuccessStatusCode();

        (await client.DeleteAsync("/api/admin/studio-users/clara", Ct)).EnsureSuccessStatusCode();

        using var fresh = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = "clara", password = "s3cret" }, Ct)).StatusCode);
    }

    // --- what the environment owns ----------------------------------------------------------------

    /// The same deal an environment connection gets: shown, never edited from here, and the refusal
    /// names the variable that owns it.
    [Fact]
    public async Task An_account_from_the_environment_is_read_only()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        var edited = await client.PutAsJsonAsync("/api/admin/studio-users/boss",
            new { role = "viewer", connections = Array.Empty<string>() }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, edited.StatusCode);
        Assert.Contains("WDS_USERS", await edited.Content.ReadAsStringAsync(Ct));

        var removed = await client.DeleteAsync("/api/admin/studio-users/boss", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
    }

    // --- the lockout guards -----------------------------------------------------------------------

    /// A studio nobody can administer any more is a studio that needs a container rollout to get
    /// back. Both ways of getting there are refused.
    [Fact]
    public async Task The_last_admin_cannot_be_removed()
    {
        using var factory = Factory(("WDS_USERS", ""),
            ("WDS_USER", ""), ("WDS_PASSWORD", ""));

        // An anonymous studio: everybody is an admin, which is how the first account gets made.
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("boss", "hunter2", "admin"), Ct)).EnsureSuccessStatusCode();

        using var signedIn = await SignedInAsync(factory);

        var removed = await signedIn.DeleteAsync("/api/admin/studio-users/boss", Ct);

        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.Contains("last admin", await removed.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        using var factory = Factory(("WDS_USERS", ""), ("WDS_USER", ""), ("WDS_PASSWORD", ""));
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("boss", "hunter2", "admin"), Ct)).EnsureSuccessStatusCode();

        using var signedIn = await SignedInAsync(factory);

        var demoted = await signedIn.PutAsJsonAsync("/api/admin/studio-users/boss",
            new { role = "viewer", connections = Array.Empty<string>() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, demoted.StatusCode);
        Assert.Contains("last admin", await demoted.Content.ReadAsStringAsync(Ct));
    }

    /// An admin in the environment counts as an admin: with one of those around, a stored admin may
    /// go, because a rollout is not needed to get back in.
    [Fact]
    public async Task A_stored_admin_may_go_while_the_environment_has_one()
    {
        using var factory = Factory();
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/admin/studio-users",
            Account("second", "s3cret", "admin"), Ct)).EnsureSuccessStatusCode();

        (await client.DeleteAsync("/api/admin/studio-users/second", Ct)).EnsureSuccessStatusCode();
    }

    // --- who may do any of this -------------------------------------------------------------------

    [Fact]
    public async Task An_editor_cannot_manage_accounts()
    {
        using var factory = Factory();
        using var admin = await SignedInAsync(factory);

        (await admin.PostAsJsonAsync("/api/admin/studio-users",
            Account("clara", "s3cret", "editor"), Ct)).EnsureSuccessStatusCode();

        using var clara = await SignedInAsync(factory, "clara", "s3cret");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await clara.GetAsync("/api/admin/studio-users", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await clara.PostAsJsonAsync("/api/admin/studio-users",
            Account("mallory", "s3cret", "admin"), Ct)).StatusCode);
    }
}

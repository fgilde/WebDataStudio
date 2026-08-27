using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Signing in to Azure SQL, Synapse or Fabric as a person, from a studio in a container where no
/// browser can be opened: the studio shows a code, somebody enters it elsewhere, the token lands
/// here — in memory, never on disk and never in the browser.
public class EntraSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// A credential that behaves like the real one without a tenant: it reports a code and then
    /// hands back a token.
    private sealed class FakeCredential(TimeSpan lifetime, string? failWith = null) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
            GetTokenAsync(context, ct).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext context, CancellationToken ct)
        {
            await Task.Yield();

            if (failWith is { } message) throw new AuthenticationFailedException(message);

            return new AccessToken("a-token", DateTimeOffset.UtcNow.Add(lifetime));
        }
    }

    private static EntraSignIn SignIn(TimeSpan lifetime, string? failWith = null) =>
        new(NullLogger<EntraSignIn>.Instance)
        {
            CredentialFactory = (tenant, report) =>
            {
                // The real credential reports the code before it starts polling; so does this one.
                report(new DeviceCode("ABCD-EFGH", "https://microsoft.com/devicelogin",
                    "Enter ABCD-EFGH at https://microsoft.com/devicelogin",
                    DateTimeOffset.UtcNow.AddMinutes(15))).GetAwaiter().GetResult();

                return new FakeCredential(lifetime, failWith);
            },
        };

    private static async Task<EntraStatus> SettledAsync(EntraSignIn entra, string id)
    {
        // The flow runs on its own thread: this waits for it rather than sleeping a fixed time.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var status = entra.Status(id);
            if (status.State is "signed-in" or "failed") return status;

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("the sign-in never settled");
    }

    [Fact]
    public async Task A_sign_in_shows_a_code_first_and_a_token_afterwards()
    {
        var entra = SignIn(TimeSpan.FromHours(1));

        var started = entra.Start("c1", null, Ct);

        // The call returns before anybody has typed anything: that is the point of the flow.
        Assert.Contains(started.State, new[] { "starting", "pending" });

        var settled = await SettledAsync(entra, "c1");

        Assert.Equal("signed-in", settled.State);
        Assert.Equal("a-token", entra.TokenFor("c1"));
    }

    [Fact]
    public void The_code_and_the_url_reach_the_browser_and_the_token_does_not()
    {
        var entra = SignIn(TimeSpan.FromHours(1));
        entra.Start("c2", null, Ct);

        // Whichever of the two states this catches, neither carries a token.
        var status = entra.Status("c2");

        Assert.Null(status.Error);
        Assert.DoesNotContain("a-token", System.Text.Json.JsonSerializer.Serialize(status));

        if (status.State != "pending") return;

        Assert.Equal("ABCD-EFGH", status.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", status.VerificationUrl);
    }

    [Fact]
    public async Task A_failed_sign_in_says_what_went_wrong()
    {
        var entra = SignIn(TimeSpan.FromHours(1), failWith: "the code expired");
        entra.Start("c3", null, Ct);

        var settled = await SettledAsync(entra, "c3");

        Assert.Equal("failed", settled.State);
        Assert.Contains("the code expired", settled.Error);
        Assert.Null(entra.TokenFor("c3"));
    }

    [Fact]
    public async Task A_token_that_is_about_to_expire_counts_as_no_token()
    {
        // Not "expired": a token with thirty seconds left would be handed to a connection that then
        // fails halfway through somebody's query.
        var entra = SignIn(TimeSpan.FromSeconds(30));
        entra.Start("c4", null, Ct);

        // Reading the status is what notices the expiry, and it clears the entry as it does, so the
        // first settled answer is the one to assert on.
        var settled = await SettledOrExpiredAsync(entra, "c4");

        Assert.Equal("expired", settled.State);
        Assert.Null(entra.TokenFor("c4"));
    }

    private static async Task<EntraStatus> SettledOrExpiredAsync(EntraSignIn entra, string id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var status = entra.Status(id);
            if (status.State is not ("starting" or "pending")) return status;

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("the sign-in never settled");
    }

    [Fact]
    public async Task Signing_out_forgets_the_token()
    {
        var entra = SignIn(TimeSpan.FromHours(1));
        entra.Start("c5", null, Ct);
        await SettledAsync(entra, "c5");

        entra.SignOut("c5");

        Assert.Null(entra.TokenFor("c5"));
        Assert.Equal("none", entra.Status("c5").State);
    }

    [Fact]
    public void A_connection_nobody_signed_in_to_has_no_token_and_no_status() =>
        Assert.Equal("none", new EntraSignIn(NullLogger<EntraSignIn>.Instance).Status("nobody").State);
}

/// Which connection strings mean "a person signs in", and what has to come out of them before a
/// token can be used.
public class EntraConnectionStringTests
{
    [Theory]
    [InlineData("""Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Device Code Flow";Database=db""")]
    [InlineData("""Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Interactive";Database=db""")]
    public void An_interactive_method_needs_a_person(string connectionString) =>
        Assert.True(EntraConnectionString.WantsAPerson(connectionString));

    [Theory]
    // The machine's own identity, a service principal, a password: all of these open without anybody
    // being at a browser.
    [InlineData("""Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Default";Database=db""")]
    [InlineData("""Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Managed Identity";Database=db""")]
    [InlineData("Server=localhost;Database=db;User Id=sa;Password=pw")]
    [InlineData("postgres://user:pw@localhost/db")]
    public void Everything_else_does_not(string connectionString) =>
        Assert.False(EntraConnectionString.WantsAPerson(connectionString));

    [Fact]
    public void A_token_replaces_the_authentication_keyword_rather_than_joining_it()
    {
        var stripped = EntraConnectionString.WithoutAuthentication(
            """Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Interactive";User Id=me@example.com;Database=db;Encrypt=True""");

        // SqlClient refuses a connection that carries both an access token and an Authentication=
        // keyword, and the user id with it.
        Assert.DoesNotContain("Authentication", stripped);
        Assert.DoesNotContain("User ID", stripped);
        Assert.Contains("Initial Catalog=db", stripped);
        Assert.Contains("Encrypt=True", stripped);
    }

    [Fact]
    public void A_connection_that_needs_a_person_says_so_in_its_own_listing()
    {
        var spec = new ConnectionSpec("id", "Azure SQL", "sqlserver",
            """Server=tcp:x.database.windows.net,1433;Authentication="Active Directory Device Code Flow";Database=db""",
            false, null, null, ConnectionSource.Stored);

        Assert.True(ConnectionRegistry.ToDto(spec).Interactive);
    }
}

/// The connection strings nobody remembers.
public class ConnectionPresetTests
{
    [Fact]
    public void The_azure_services_are_all_there()
    {
        var ids = ConnectionPresets.For(null).Select(preset => preset.Id).ToList();

        foreach (var expected in new[]
                 {
                     "azure-sql-identity", "azure-sql-interactive", "synapse-serverless",
                     "synapse-dedicated", "fabric-warehouse",
                 })
            Assert.Contains(expected, ids);
    }

    [Fact]
    public void A_preset_is_filtered_by_engine() =>
        Assert.All(ConnectionPresets.For("storage"),
            preset => Assert.Equal("storage", preset.Engine));

    [Fact]
    public void Every_preset_that_needs_a_person_says_so_in_its_connection_string()
    {
        foreach (var preset in ConnectionPresets.For("sqlserver").Where(p => p.Interactive))
            Assert.True(EntraConnectionString.WantsAPerson(preset.Template.Replace("{server}", "x")
                    .Replace("{database}", "db").Replace("{workspace}", "w")
                    .Replace("{endpoint}", "e").Replace("{warehouse}", "wh")),
                preset.Id);
    }

    [Fact]
    public void And_no_preset_that_does_not_need_one_pretends_to()
    {
        foreach (var preset in ConnectionPresets.For("sqlserver").Where(p => !p.Interactive))
            Assert.False(EntraConnectionString.WantsAPerson(preset.Template.Replace("{server}", "x")
                    .Replace("{database}", "db").Replace("{workspace}", "w").Replace("{pool}", "p")
                    .Replace("{user}", "u").Replace("{password}", "pw")),
                preset.Id);
    }
}

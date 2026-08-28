using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What a provider's answer becomes in the studio. Pure: no tenant needed to pin down the mapping.
public class OidcOptionTests
{
    private static OidcOptions Options(params (string Key, string Value)[] settings) =>
        OidcOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                pair => pair.Key, pair => (string?)pair.Value))
            .Build());

    private static readonly (string, string)[] Minimal =
    [
        ("WDS_OIDC_AUTHORITY", "https://login.microsoftonline.com/tenant/v2.0"),
        ("WDS_OIDC_CLIENT_ID", "studio"),
    ];

    [Fact]
    public void Nothing_configured_is_no_provider() =>
        Assert.False(Options().Enabled);

    [Fact]
    public void Half_a_configuration_is_no_provider_either()
    {
        // Locking everybody out of a studio because one variable was forgotten is the wrong failure.
        Assert.False(Options(("WDS_OIDC_AUTHORITY", "https://example.test/")).Enabled);
        Assert.False(Options(("WDS_OIDC_CLIENT_ID", "studio")).Enabled);
    }

    [Fact]
    public void An_authority_and_a_client_id_are_enough()
    {
        var options = Options(Minimal);

        Assert.True(options.Enabled);
        Assert.Equal(["openid", "profile", "email"], options.Scopes);
        Assert.Equal("Single sign-on", options.Label);
        Assert.Equal("/signin-oidc", options.CallbackPath);
        // A viewer until the deployment says otherwise: the safe end of the three roles.
        Assert.Equal(UserRoles.Viewer, options.DefaultRole);
        Assert.True(options.RequireHttpsMetadata);
    }

    [Fact]
    public void Scopes_a_label_and_a_callback_can_be_said()
    {
        var options = Options([
            .. Minimal,
            ("WDS_OIDC_SCOPES", "openid, profile, groups"),
            ("WDS_OIDC_LABEL", "Sign in with Entra"),
            ("WDS_OIDC_CALLBACK_PATH", "/oidc/back"),
            ("WDS_OIDC_REQUIRE_HTTPS", "false"),
        ]);

        Assert.Equal(["openid", "profile", "groups"], options.Scopes);
        Assert.Equal("Sign in with Entra", options.Label);
        Assert.Equal("/oidc/back", options.CallbackPath);
        Assert.False(options.RequireHttpsMetadata);
    }

    private static OidcOptions WithGroups() =>
        Options([
            .. Minimal,
            ("WDS_OIDC_ADMINS", "dba-group, ada@example.com"),
            ("WDS_OIDC_EDITORS", "developers"),
            ("WDS_OIDC_VIEWERS", "everyone"),
        ]);

    [Fact]
    public void A_group_becomes_a_role() =>
        Assert.Equal(UserRoles.Editor,
            WithGroups().RoleFor([new Claim("groups", "developers")]));

    [Fact]
    public void So_does_an_address_in_a_tenant_with_no_groups() =>
        Assert.Equal(UserRoles.Admin,
            WithGroups().RoleFor([new Claim("preferred_username", "ada@example.com")]));

    [Fact]
    public void Two_groups_give_the_one_that_was_meant() =>
        Assert.Equal(UserRoles.Admin, WithGroups().RoleFor([
            new Claim("groups", "everyone"),
            new Claim("groups", "developers"),
            new Claim("groups", "dba-group"),
        ]));

    [Fact]
    public void Matching_is_not_case_sensitive_because_a_directory_is_not() =>
        Assert.Equal(UserRoles.Editor,
            WithGroups().RoleFor([new Claim("roles", "Developers")]));

    [Fact]
    public void Matching_nothing_gives_the_default_role()
    {
        Assert.Equal(UserRoles.Viewer, WithGroups().RoleFor([new Claim("groups", "interns")]));

        var friendly = Options([.. Minimal, ("WDS_OIDC_DEFAULT_ROLE", "editor")]);
        Assert.Equal(UserRoles.Editor, friendly.RoleFor([]));
    }

    [Fact]
    public void A_claim_that_is_not_about_identity_is_not_matched_against() =>
        // The tenant id happening to equal a group name should not hand out the admin role.
        Assert.Equal(UserRoles.Viewer, WithGroups().RoleFor([new Claim("tid", "dba-group")]));

    [Fact]
    public void The_name_is_the_one_the_person_would_recognise()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "8f2c-not-a-name"),
            new Claim("name", "Ada Lovelace"),
            new Claim("preferred_username", "ada@example.com"),
        ]));

        Assert.Equal("ada@example.com", OidcOptions.NameFor(principal));
    }

    [Fact]
    public void And_the_identifier_where_there_is_nothing_else() =>
        Assert.Equal("8f2c", OidcOptions.NameFor(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "8f2c")]))));

    [Fact]
    public void A_provider_account_sees_every_connection()
    {
        // Which connections an account may see is a studio-side list; a provider cannot know it.
        var user = WithGroups().UserFor(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("preferred_username", "ada@example.com"),
        ])));

        Assert.Equal("ada@example.com", user.Name);
        Assert.True(user.IsAdmin);
        Assert.Empty(user.Connections);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/connections", "/connections")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    // An open redirect is how a login flow becomes somebody else's phishing page.
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    public void Only_a_path_on_this_studio_is_returned_to(string? target, string expected) =>
        Assert.Equal(expected, OidcOptions.SafeReturn(target));

    [Fact]
    public void A_provider_counts_as_credentials()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            Minimal.ToDictionary(pair => pair.Item1, pair => (string?)pair.Item2)).Build();

        var users = UserStore.FromConfiguration(config);

        // Otherwise configuring a provider would leave the studio wide open with a login button.
        Assert.False(users.Anonymous);
        Assert.True(users.External);
        Assert.Empty(users.All);
    }
}

/// End to end, as far as it goes without a tenant: what the studio says about the provider, and that
/// configuring one closes the door.
public class OidcEndpointTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-oidc").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory(params (string Key, string? Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting rather than ConfigureAppConfiguration: the authentication handler is
            // registered while the app is being built, so its configuration has to be there by then.
            b.UseSetting("DB_PATH", Path.Combine(_dir, "wds.db"));
            foreach (var (key, value) in extra) b.UseSetting(key, value);
        });

    private static readonly (string, string?)[] Provider =
    [
        ("WDS_OIDC_AUTHORITY", "https://login.microsoftonline.com/tenant/v2.0"),
        ("WDS_OIDC_CLIENT_ID", "studio"),
        ("WDS_OIDC_LABEL", "Sign in with Entra"),
    ];

    [Fact]
    public async Task The_login_screen_is_told_which_provider_to_offer()
    {
        using var factory = Factory(Provider);
        using var client = factory.CreateClient();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        var sso = me.GetProperty("sso");

        Assert.True(sso.GetProperty("enabled").GetBoolean());
        Assert.Equal("Sign in with Entra", sso.GetProperty("label").GetString());
        // No local accounts: the screen has nothing else to show.
        Assert.True(sso.GetProperty("only").GetBoolean());
        Assert.False(me.GetProperty("anonymous").GetBoolean());
        Assert.False(me.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task A_provider_alongside_local_accounts_leaves_both_ways_in()
    {
        using var factory = Factory([.. Provider,
            ("WDS_USER", "ada"), ("WDS_PASSWORD", "secret-secret")]);
        using var client = factory.CreateClient();

        var sso = (await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("sso");

        Assert.True(sso.GetProperty("enabled").GetBoolean());
        Assert.False(sso.GetProperty("only").GetBoolean());
    }

    [Fact]
    public async Task Configuring_a_provider_closes_the_door()
    {
        using var factory = Factory(Provider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/connections", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_provider_there_is_nothing_to_sign_in_to()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.False(me.GetProperty("sso").GetProperty("enabled").GetBoolean());
        // And an open studio stays open.
        Assert.True(me.GetProperty("anonymous").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/auth/sso", Ct)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_goes_to_the_provider()
    {
        using var factory = Factory(Provider);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // The metadata document is fetched from a tenant that is not there, so what this pins down
        // is that the challenge is the provider's rather than a 404 or the login form again.
        var response = await client.GetAsync("/api/auth/sso?returnUrl=/connections", Ct);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}

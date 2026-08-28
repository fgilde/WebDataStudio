using System.Security.Claims;

namespace WebDataStudio.Server.Services;

/// Signing in to the studio itself with the identity provider a company already has.
///
/// `WDS_USERS` is a list of accounts in a container's environment: fine for one team, wrong for an
/// organisation that already decides who works there somewhere else. With an authority and a client
/// id configured, the login screen offers that provider instead — Entra, Keycloak, Auth0, Okta,
/// anything that speaks OpenID Connect — and the studio never sees a password.
///
/// The role still belongs to the studio: `WDS_OIDC_ADMINS`, `WDS_OIDC_EDITORS` and
/// `WDS_OIDC_VIEWERS` are matched against the groups, roles and addresses the provider sends, and
/// anybody who matches nothing gets `WDS_OIDC_DEFAULT_ROLE` (a viewer unless said otherwise).
public sealed record OidcOptions(
    bool Enabled,
    string Authority,
    string ClientId,
    string ClientSecret,
    /// What the button on the login screen says.
    string Label,
    IReadOnlyList<string> Scopes,
    string DefaultRole,
    IReadOnlySet<string> Admins,
    IReadOnlySet<string> Editors,
    IReadOnlySet<string> Viewers,
    /// False lets a provider serve its metadata over http — a test provider on a laptop, never a
    /// tenant on the internet.
    bool RequireHttpsMetadata,
    string CallbackPath,
    /// Why this configuration cannot be used, or null when it can. A provider nobody can reach must
    /// leave the studio working: the login screen simply does not offer it, and the log says why.
    string? Problem = null)
{
    public const string Scheme = "oidc";

    public static OidcOptions FromConfiguration(IConfiguration config)
    {
        var authority = config["WDS_OIDC_AUTHORITY"]?.Trim() ?? "";
        var clientId = config["WDS_OIDC_CLIENT_ID"]?.Trim() ?? "";

        var scopes = Split(config["WDS_OIDC_SCOPES"]);
        if (scopes.Count == 0) scopes = ["openid", "profile", "email"];

        var label = config["WDS_OIDC_LABEL"]?.Trim();
        var callback = config["WDS_OIDC_CALLBACK_PATH"]?.Trim();

        var requireHttps = !string.Equals(config["WDS_OIDC_REQUIRE_HTTPS"], "false",
            StringComparison.OrdinalIgnoreCase);

        // Both halves or nothing: an authority without a client id cannot start a sign-in, and half
        // a configuration should not lock everybody out of a studio.
        var configured = authority.Length > 0 && clientId.Length > 0;

        return new OidcOptions(
            Enabled: configured && Refuse(authority, requireHttps) is null,
            authority,
            clientId,
            config["WDS_OIDC_CLIENT_SECRET"]?.Trim() ?? "",
            string.IsNullOrEmpty(label) ? "Single sign-on" : label,
            scopes,
            UserRoles.Normalise(config["WDS_OIDC_DEFAULT_ROLE"]),
            Set(config["WDS_OIDC_ADMINS"]),
            Set(config["WDS_OIDC_EDITORS"]),
            Set(config["WDS_OIDC_VIEWERS"]),
            requireHttps,
            string.IsNullOrEmpty(callback) ? "/signin-oidc" : callback,
            configured ? Refuse(authority, requireHttps) : null);
    }

    /// Why this pair cannot be used, or null.
    ///
    /// The handler validates its own options the first time any request passes through
    /// authentication — not on the sign-in path, on *every* path. So a provider configured wrongly
    /// used to answer 500 for the whole studio, including the login screen it was meant to improve.
    /// Whatever cannot work is refused here instead, before the handler is ever registered.
    private static string? Refuse(string authority, bool requireHttps)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttps && issuer.Scheme != Uri.UriSchemeHttp))
            return $"WDS_OIDC_AUTHORITY is not an issuer URL: {authority}";

        return issuer.Scheme == Uri.UriSchemeHttp && requireHttps
            ? "WDS_OIDC_AUTHORITY is http, which a provider on the internet never is. "
              + "Set WDS_OIDC_REQUIRE_HTTPS=false if this is a provider on your own machine."
            : null;
    }

    private static List<string> Split(string? value) =>
        [.. (value ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlySet<string> Set(string? value) =>
        new HashSet<string>(Split(value), StringComparer.OrdinalIgnoreCase);

    /// The claims a role can be read from: what the provider says about groups and roles, and who
    /// the person is — so `WDS_OIDC_ADMINS=ada@example.com` works in a tenant with no groups.
    private static readonly string[] RoleClaims =
    [
        "roles", "role", ClaimTypes.Role, "groups", "group", "wids",
        "preferred_username", "email", ClaimTypes.Email, ClaimTypes.Upn, ClaimTypes.Name,
    ];

    /// The studio role for whoever just signed in. Admin beats editor beats viewer, so somebody in
    /// two groups gets the one that was meant.
    public string RoleFor(IEnumerable<Claim> claims)
    {
        var values = claims
            .Where(claim => RoleClaims.Contains(claim.Type))
            .Select(claim => claim.Value)
            .ToList();

        if (values.Any(Admins.Contains)) return UserRoles.Admin;
        if (values.Any(Editors.Contains)) return UserRoles.Editor;
        if (values.Any(Viewers.Contains)) return UserRoles.Viewer;

        return DefaultRole;
    }

    /// What to call this person in the studio: what they would recognise, not the provider's
    /// identifier for them.
    public static string NameFor(ClaimsPrincipal principal) =>
        First(principal, "preferred_username")
        ?? First(principal, ClaimTypes.Upn)
        ?? First(principal, "email")
        ?? First(principal, ClaimTypes.Email)
        ?? First(principal, "name")
        ?? First(principal, ClaimTypes.Name)
        ?? First(principal, "sub")
        ?? First(principal, ClaimTypes.NameIdentifier)
        ?? "unknown";

    private static string? First(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;

    /// The studio account a provider's answer becomes. Every connection: which ones an account may
    /// see is a studio-side list, and a provider has no way to know about it.
    public StudioUser UserFor(ClaimsPrincipal principal) =>
        new(NameFor(principal), "", RoleFor(principal.Claims),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// Where to go once the sign-in is done. Only a path on this studio: an open redirect is how a
    /// login flow becomes somebody else's phishing page.
    public static string SafeReturn(string? target) =>
        target is { Length: > 0 } && target.StartsWith('/') && !target.StartsWith("//")
            ? target
            : "/";
}

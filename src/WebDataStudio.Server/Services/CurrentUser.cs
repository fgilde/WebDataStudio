using System.Security.Claims;

namespace WebDataStudio.Server.Services;

/// The account behind the request being handled, or null when the studio runs open or the request
/// is one of the few that need no account (health, login, the SPA itself).
///
/// The role and the allowed connections ride in the sign-in cookie, so a request needs no lookup
/// and a rollout that removes an account takes effect at its next sign-in.
public sealed class CurrentUser(IHttpContextAccessor accessor, UserStore users)
{
    public const string RoleClaim = "wds:role";
    public const string ConnectionsClaim = "wds:connections";

    public StudioUser? User
    {
        get
        {
            if (users.Anonymous) return null;

            var principal = accessor.HttpContext?.User;
            var name = principal?.Identity?.Name;
            if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(name)) return null;

            var connections = principal.FindFirst(ConnectionsClaim)?.Value ?? "";

            return new StudioUser(name, "", UserRoles.Normalise(principal.FindFirst(RoleClaim)?.Value),
                new HashSet<string>(
                    connections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    /// The claims a signed-in account carries.
    public static IEnumerable<Claim> ClaimsOf(StudioUser user) =>
    [
        new(ClaimTypes.Name, user.Name),
        new(RoleClaim, user.Role),
        new(ConnectionsClaim, string.Join(",", user.Connections)),
    ];
}

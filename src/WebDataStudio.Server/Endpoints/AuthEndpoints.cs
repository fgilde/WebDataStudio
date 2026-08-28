using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AuthEndpoints
{
    public record LoginRequest(string Username, string Password);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var users = app.Services.GetRequiredService<UserStore>();
        var api = app.MapGroup("/api");

        // A name for this studio, shown in the header and the browser tab. Empty by default, so
        // an unnamed studio looks exactly as it did before.
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var title = configuration["WDS_TITLE"]?.Trim();
        if (string.IsNullOrEmpty(title)) title = null;

        // The theme a deployment wants the studio to come up in. The list of themes lives in the
        // client, so the id travels unchecked and the client ignores one it does not know rather
        // than this refusing to start over a colour scheme.
        var theme = configuration["WDS_THEME"]?.Trim();
        if (string.IsNullOrEmpty(theme)) theme = null;

        var oidc = app.Services.GetRequiredService<OidcOptions>();

        api.MapGet("/auth/me", (HttpContext ctx, CurrentUser current) => Results.Ok(new
        {
            anonymous = users.Anonymous,
            authenticated = users.Anonymous || (ctx.User.Identity?.IsAuthenticated ?? false),
            username = users.Anonymous ? null : ctx.User.Identity?.Name,
            // The UI hides what a role cannot reach; the server refuses it anyway.
            role = current.User?.Role,
            // The login screen needs it too, so it rides along with the one call that always runs.
            title,
            // Where to start. A person's own choice is kept in their browser and wins over this.
            theme,
            // Whether there is a provider to offer, and what its button says. A studio with a
            // provider and no local accounts shows only that button.
            sso = new { enabled = oidc.Enabled, label = oidc.Label, only = oidc.Enabled && users.All.Count == 0 },
        })).AllowAnonymous();

        // A top-level navigation rather than a fetch: the provider answers with its own page, and a
        // redirect cannot be followed out of an XMLHttpRequest.
        api.MapGet("/auth/sso", (HttpContext ctx, string? returnUrl) =>
            oidc.Enabled
                ? Results.Challenge(
                    new AuthenticationProperties { RedirectUri = OidcOptions.SafeReturn(returnUrl) },
                    [OidcOptions.Scheme])
                : Results.NotFound(new { message = "no identity provider is configured" }))
            .AllowAnonymous();

        api.MapPost("/auth/login", async (HttpContext ctx, LoginRequest body) =>
        {
            if (users.Anonymous) return Results.Ok(new { anonymous = true });

            // Verification is constant-time and costs the same for an unknown name as for a wrong
            // password, so neither can be found by timing.
            var user = users.Verify(body.Username, body.Password);
            if (user is null)
                return Results.Json(new { message = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var identity = new ClaimsIdentity(CurrentUser.ClaimsOf(user),
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Ok(new
            {
                anonymous = false, authenticated = true, username = user.Name, role = user.Role,
            });
        }).AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();
    }

}

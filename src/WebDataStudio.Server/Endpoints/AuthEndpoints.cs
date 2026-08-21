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
        var title = app.Services.GetRequiredService<IConfiguration>()["WDS_TITLE"]?.Trim();
        if (string.IsNullOrEmpty(title)) title = null;

        api.MapGet("/auth/me", (HttpContext ctx, CurrentUser current) => Results.Ok(new
        {
            anonymous = users.Anonymous,
            authenticated = users.Anonymous || (ctx.User.Identity?.IsAuthenticated ?? false),
            username = users.Anonymous ? null : ctx.User.Identity?.Name,
            // The UI hides what a role cannot reach; the server refuses it anyway.
            role = current.User?.Role,
            // The login screen needs it too, so it rides along with the one call that always runs.
            title,
        })).AllowAnonymous();

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

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AuthEndpoints
{
    public record LoginRequest(string Username, string Password);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AuthOptions>();
        var api = app.MapGroup("/api");

        api.MapGet("/auth/me", (HttpContext ctx) => Results.Ok(new
        {
            anonymous = options.Anonymous,
            authenticated = options.Anonymous || (ctx.User.Identity?.IsAuthenticated ?? false),
            username = options.Anonymous ? null : ctx.User.Identity?.Name,
        })).AllowAnonymous();

        api.MapPost("/auth/login", async (HttpContext ctx, LoginRequest body) =>
        {
            if (options.Anonymous) return Results.Ok(new { anonymous = true });

            // Constant-time comparison so a wrong password cannot be found by timing.
            var userOk = FixedTimeEquals(body.Username, options.Username!);
            var passOk = FixedTimeEquals(body.Password, options.Password!);
            if (!userOk || !passOk)
                return Results.Json(new { message = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, options.Username!) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { anonymous = false, authenticated = true, username = options.Username });
        }).AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

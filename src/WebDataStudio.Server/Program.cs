using Microsoft.AspNetCore.Authentication.Cookies;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Endpoints;
using WebDataStudio.Server.Export;
using WebDataStudio.Server.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

// Enums travel as their names: "Table", not 8. The SPA switches on these strings.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Resolved from DI rather than from builder.Configuration: configuration sources added by a host
// builder (WebApplicationFactory in tests, and anything layered on later) only land in the composed
// IConfiguration after Build().
builder.Services.AddSingleton(sp => AuthOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "wds.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        // An API returns 401/403; it must not redirect a fetch() to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

builder.Services.AddAuthorization();

// Same reason as AuthOptions: DB_PATH is only final once the host has composed its configuration.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dbPath = config["DB_PATH"] ?? "/data/webdatastudio.db";
    var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
    Directory.CreateDirectory(dataDir);
    return new SecretProtector(dataDir, config["WDS_SECRET_KEY"]);
});
builder.Services.AddSingleton(sp => new ConnectionStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? "/data/webdatastudio.db",
    sp.GetRequiredService<SecretProtector>()));
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddSingleton<SessionFactory>();
builder.Services.AddSingleton<QueryRunner>();
builder.Services.AddSingleton<ExporterRegistry>();
builder.Services.AddSingleton(sp => new WorkspaceStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? "/data/webdatastudio.db"));

var app = builder.Build();

app.MapOpenApi();

var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version })).AllowAnonymous();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// The whole API needs a signed-in user when credentials are configured. Health and the auth
// endpoints stay open so the SPA can load and log in; without credentials nothing is guarded.
app.Use(async (ctx, next) =>
{
    var options = ctx.RequestServices.GetRequiredService<AuthOptions>();
    var path = ctx.Request.Path;
    var open = !path.StartsWithSegments("/api")
               || path.StartsWithSegments("/api/auth")
               || path.StartsWithSegments("/api/health");

    if (!options.Anonymous && !open && !(ctx.User.Identity?.IsAuthenticated ?? false))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapAuthEndpoints();
app.MapConnectionEndpoints();
app.MapSchemaEndpoints();
app.MapQueryEndpoints();
app.MapWorkspaceEndpoints();
app.MapExportEndpoints();

app.MapMethods("/api/{**rest}", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "PATCH" }, () => Results.NotFound());
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { } // exposed for WebApplicationFactory

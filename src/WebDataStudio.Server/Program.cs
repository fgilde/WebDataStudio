using System.Diagnostics;
using System.Reflection;
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

// A desktop build keeps its data beside the binary; a container keeps it on the /data volume.
AppContext.TryGetSwitch("Wds.Desktop", out var desktop);
var defaultDbPath = desktop
    ? Path.Combine(AppContext.BaseDirectory, "data", "webdatastudio.db")
    : "/data/webdatastudio.db";

// Same reason as AuthOptions: DB_PATH is only final once the host has composed its configuration.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dbPath = config["DB_PATH"] ?? defaultDbPath;
    var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
    Directory.CreateDirectory(dataDir);
    return new SecretProtector(dataDir, config["WDS_SECRET_KEY"]);
});
builder.Services.AddSingleton(sp => new ConnectionStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? defaultDbPath,
    sp.GetRequiredService<SecretProtector>()));
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton(sp => new SessionPool(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SessionFactory>();
builder.Services.AddSingleton<QueryRunner>();
builder.Services.AddSingleton<ExporterRegistry>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(sp => new WorkspaceStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? "/data/webdatastudio.db"));

var app = builder.Build();

app.MapOpenApi();

// The informational version carries the commit ("1.1.42+abc1234"), which is what answers whether
// a running container is the build somebody just pushed.
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var built = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location);
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version,
    commit = version.Contains('+') ? version.Split('+')[1] : null,
    built,
})).AllowAnonymous();

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
app.MapImportEndpoints();
app.MapDataEndpoints();
app.MapAnalysisEndpoints();
app.MapDdlEndpoints();
app.MapCompareEndpoints();
app.MapAdminEndpoints();
app.MapDiagramEndpoints();
app.MapSavedQueryEndpoints();

app.MapMethods("/api/{**rest}", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "PATCH" }, () => Results.NotFound());
app.MapFallbackToFile("index.html").AllowAnonymous();

// Desktop mode: the same server, started from a downloaded binary rather than a container, opens
// the browser once it is listening. In a container there is no browser to open.
if ((desktop || string.Equals(Environment.GetEnvironmentVariable("WDS_OPEN_BROWSER"), "true",
        StringComparison.OrdinalIgnoreCase))
    && !string.Equals(Environment.GetEnvironmentVariable("WDS_OPEN_BROWSER"), "false",
        StringComparison.OrdinalIgnoreCase))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:8080";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            app.Logger.LogInformation("open {Url} in your browser ({Reason})", url, e.Message);
        }
    });
}

app.Run();

public partial class Program { } // exposed for WebApplicationFactory

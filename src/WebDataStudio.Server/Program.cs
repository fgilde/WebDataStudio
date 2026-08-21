using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Endpoints;
using WebDataStudio.Server.Export;
using WebDataStudio.Server.Mcp;
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
builder.Services.AddSingleton(sp => UserStore.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CurrentUser>();

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
    // The directory is created by whoever can: SecretProtector falls back to a key in memory and
    // the stores report the path they could not use, so a read-only /data starts rather than
    // crashing on the way up.
    var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
    return new SecretProtector(dataDir, config["WDS_SECRET_KEY"]);
});
builder.Services.AddSingleton(sp => new ConnectionStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? defaultDbPath,
    sp.GetRequiredService<SecretProtector>()));
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<MaskPolicyStore>();
builder.Services.AddSingleton<UndoStore>();
builder.Services.AddSingleton<Federation>();
builder.Services.AddSingleton(sp => AssistantOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<Assistant>();
builder.Services.AddSingleton(sp => McpOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<McpToolbox>();
builder.Services.AddSingleton<McpAvailability>();
builder.Services.AddSingleton(sp => AlertOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<HealthAlertSink>();
builder.Services.AddSingleton<HealthAlerts>();
builder.Services.AddSingleton(sp => SnapshotOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SchemaSnapshots>();
builder.Services.AddHostedService<SchemaSnapshotStartup>();
builder.Services.AddSingleton(sp => SavedQueryImportOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SavedQueryImport>();
builder.Services.AddHostedService<SavedQueryImportStartup>();
builder.Services.AddSingleton(sp => SeedOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SeedScripts>();
builder.Services.AddHostedService<SeedScriptStartup>();
builder.Services.AddSingleton(sp => ScheduleOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ScheduledQueries>();
builder.Services.AddHostedService<ScheduledQueryRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthAlerts>());
builder.Services.AddHttpClient("alerts", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("assist", client => client.Timeout = TimeSpan.FromSeconds(60));
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

// Touch both stores now rather than on the first request that needs one. They are singletons, so
// a /data that never answers would otherwise turn one unlucky request into a hang and every
// following one into a queue behind it — which is exactly how an Azure Files mount fails.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebDataStudio");
var connectionStore = app.Services.GetRequiredService<ConnectionStore>();
var workspaceStore = app.Services.GetRequiredService<WorkspaceStore>();

foreach (var (label, path, error) in new[]
         {
             ("connections", connectionStore.Path, connectionStore.Error),
             ("workspace", workspaceStore.Path, workspaceStore.Error),
         })
{
    if (error is null) startupLog.LogInformation("{Store} database ready at {Path}", label, path);
    else startupLog.LogError("{Store} database at {Path} is not usable: {Error}", label, path, error);
}

app.MapGet("/api/health", (ConnectionRegistry registry, AssistantOptions assistantOptions,
    McpAvailability mcpAvailability, AlertOptions alertOptions) => Results.Ok(new
{
    status = connectionStore.Available && workspaceStore.Available ? "ok" : "degraded",
    version,
    commit = version.Contains('+') ? version.Split('+')[1] : null,
    built,
    // What the studio can actually do right now, so "why is my connection missing" is one call
    // away instead of a container-log expedition.
    store = new
    {
        path = connectionStore.Path,
        available = connectionStore.Available && workspaceStore.Available,
        error = connectionStore.Error ?? workspaceStore.Error,
    },
    connections = registry.All().Count,
    // Off unless configured, and said out loud: a studio that quietly talks to an endpoint would
    // be the wrong kind of surprise.
    assist = assistantOptions.Configured,
    // Whether anything is watching the health report, and how often.
    alerts = alertOptions.Configured
        ? new { intervalMinutes = (int)alertOptions.Interval.TotalMinutes, minSeverity = alertOptions.MinSeverity }
        : null,
    // The MCP endpoint, so a client can find it without being told where to look — and so a
    // studio that refuses to serve it says why instead of advertising a path that answers HTML.
    mcp = mcpAvailability.Describe(),
})).AllowAnonymous();

// A stuck data directory is a dependency failure, not a bug in the request: say so, with the path
// in the message, instead of letting a 500 stand for it.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (WorkspaceUnavailableException e)
    {
        startupLog.LogError(e, "a request needed the workspace database");
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { message = e.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// The whole API needs a signed-in user when credentials are configured. Health and the auth
// endpoints stay open so the SPA can load and log in; without credentials nothing is guarded.
app.Use(async (ctx, next) =>
{
    var users = ctx.RequestServices.GetRequiredService<UserStore>();
    var path = ctx.Request.Path;
    var open = !path.StartsWithSegments("/api")
               || path.StartsWithSegments("/api/auth")
               || path.StartsWithSegments("/api/health");

    if (!users.Anonymous && !open && !(ctx.User.Identity?.IsAuthenticated ?? false))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // The administration surface kills sessions and reads server-wide state, so it belongs to
    // admins. Everything else is bounded by which connections the account may see, and a viewer's
    // connections are read-only wherever they are opened.
    var current = ctx.RequestServices.GetRequiredService<CurrentUser>().User;
    if (current is not null && !current.IsAdmin && path.StartsWithSegments("/api/admin"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { message = "this needs the admin role" });
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
app.MapRedisEndpoints();
app.MapDiagramEndpoints();
app.MapSavedQueryEndpoints();
app.MapFederationEndpoints();
app.MapAssistantEndpoints();
app.MapMcpEndpoints();

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

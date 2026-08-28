using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Storage;
using WebDataStudio.Server.Endpoints;
using WebDataStudio.Server.Export;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WebDataStudio.Server.Mcp;
using WebDataStudio.Server.Services;

// The image build stages DuckDB's storage extensions with this, so that a studio in a private
// network never has to download one. It installs and exits; it does not start a server.
if (args.Contains("--install-storage-extensions"))
{
    var directory = Environment.GetEnvironmentVariable(DuckDbExtensions.DirectoryVariable)
                    is { Length: > 0 } given
        ? given
        : "/opt/duckdb/extensions";

    Console.WriteLine($"staging DuckDB storage extensions in {directory}");
    Console.WriteLine("loaded: " + await DuckDbExtensions.StageAsync(directory));
    return;
}

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

builder.Services.AddSingleton(sp => OidcOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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

// The company's own identity provider, where there is one. The studio keeps its cookie either way:
// what arrives from the provider is turned into the same three claims a password sign-in writes, so
// everything downstream — roles, which connections an account sees, the audit trail — is unchanged.
//
// The handler is always registered and configured from the container, because the values are only
// final once the host has composed its configuration; /api/auth/sso is what refuses when no
// provider was configured, so a registered handler nobody can reach costs nothing.
// Only when one is configured *and usable*: the authentication middleware instantiates every
// registered handler on every request, so a handler that refuses to be built takes the whole studio
// with it — see OidcOptions.Refuse for what is checked before it gets that far.
if (OidcOptions.FromConfiguration(builder.Configuration).Enabled)
    authentication.AddOpenIdConnect(OidcOptions.Scheme, o =>
    {
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // The authorization code flow with PKCE: the only one worth offering a browser in 2026.
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.MapInboundClaims = false;

        o.Events.OnTicketReceived = ctx =>
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<OidcOptions>();
            var user = options.UserFor(ctx.Principal ?? new ClaimsPrincipal());

            // Only the studio's own claims are kept: the id token's audience, expiry and the rest
            // belong to the provider, and a cookie that carries them is a cookie that leaks them.
            ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                CurrentUser.ClaimsOf(user), CookieAuthenticationDefaults.AuthenticationScheme));

            return Task.CompletedTask;
        };

        // A sign-in that goes wrong is a login screen with a message on it, not a stack trace.
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/?sso=failed");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });

builder.Services.AddOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
        OidcOptions.Scheme)
    .Configure<OidcOptions>((o, oidc) =>
    {
        o.Authority = oidc.Authority;
        o.ClientId = oidc.ClientId;
        o.ClientSecret = oidc.ClientSecret;
        o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        o.CallbackPath = oidc.CallbackPath;

        o.Scope.Clear();
        foreach (var scope in oidc.Scopes) o.Scope.Add(scope);
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
builder.Services.AddSingleton(sp => ShareOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ResultShares>();

// Traces and metrics, when a collector is configured the standard way. Nothing is exported without
// OTEL_EXPORTER_OTLP_ENDPOINT — a studio that talks to a collector nobody asked for would be the
// wrong kind of surprise, and the instrumentation costs nothing while nobody listens.
// Read twice on purpose: the exporter has to be wired while services are being registered, and the
// options a request reads have to come from the final configuration — which is only complete once
// the host is built (a test that injects settings would otherwise be told the feature is off).
var telemetryOptions = TelemetryOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(sp =>
    TelemetryOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

if (telemetryOptions.Configured)
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(telemetryOptions.ServiceName))
        .WithTracing(tracing => tracing
            .AddSource(Telemetry.SourceName)
            .AddAspNetCoreInstrumentation(instrumentation =>
                // Health checks and static files would be most of the traffic and none of the
                // information.
                instrumentation.Filter = context =>
                    !context.Request.Path.StartsWithSegments("/api/health"))
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(metrics => metrics
            .AddMeter(Telemetry.SourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthAlerts>());
builder.Services.AddHttpClient("alerts", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("assist", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton(sp => new SessionPool(sp.GetRequiredService<IConfiguration>()));
// Transactions a query tab holds open across requests. Singleton, because the session they
// hold has to survive the request that opened it.
builder.Services.AddSingleton(sp => new OpenTransactions(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<OpenTransactions>>()));
// An interactive Entra sign-in is per studio, not per request: the token it ends up with is what
// the next connection uses.
builder.Services.AddSingleton<SchemaScope>();
builder.Services.AddSingleton<EntraSignIn>();
builder.Services.AddSingleton<SessionFactory>();
builder.Services.AddSingleton(sp => AuditOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<AuditTrail>();
builder.Services.AddSingleton(sp => SafetyOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SubsetBuilder>();
builder.Services.AddSingleton<StatementCapture>();
// Rules about the data rather than about the schema: each one counts the rows that break it.
builder.Services.AddSingleton(sp => WebDataStudio.Server.Analysis.QualityFileOptions
    .FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<WebDataStudio.Server.Analysis.QualityRunner>();
// A file becomes a table: DuckDB reads it, the target engine's DDL writer creates it.
builder.Services.AddSingleton<WebDataStudio.Server.Import.ImportService>();
builder.Services.AddSingleton<WebDataStudio.Server.Import.FileTableImport>();
builder.Services.AddSingleton<QueryRunner>();
// Export formats somebody wrote themselves: a folder the deployment mounts, plus whatever was
// saved in this studio. They are text with placeholders, never code to run.
builder.Services.AddSingleton(sp => new ExportTemplates(
    sp.GetRequiredService<IConfiguration>(),
    () => sp.GetRequiredService<WorkspaceStore>().LoadItem("export-templates"),
    json => sp.GetRequiredService<WorkspaceStore>().SaveItem("export-templates", json)));

builder.Services.AddSingleton(sp => new ExporterRegistry(sp.GetRequiredService<ExportTemplates>()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(sp => new WorkspaceStore(
    sp.GetRequiredService<IConfiguration>()["DB_PATH"] ?? "/data/webdatastudio.db"));

// Archived results are files, and they live next to the workspace database unless told otherwise —
// the same volume that already has to survive a restart.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var configured = config["WDS_ARCHIVE_DIR"];
    if (configured is { Length: > 0 }) return new Archives(configured);

    var dbPath = config["DB_PATH"] ?? defaultDbPath;
    return new Archives(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "archives"));
});

var app = builder.Build();

app.MapOpenApi();

// The informational version carries the commit ("1.1.42+abc1234"), which is what answers whether
// a running container is the build somebody just pushed.
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var built = BuildStamp.Of(typeof(Program).Assembly);

// Touch both stores now rather than on the first request that needs one. They are singletons, so
// a /data that never answers would otherwise turn one unlucky request into a hang and every
// following one into a queue behind it — which is exactly how an Azure Files mount fails.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebDataStudio");

// A provider that was configured but cannot be used: the studio runs, the login screen does not
// offer it, and this is the only place that says why.
if (app.Services.GetRequiredService<OidcOptions>().Problem is { } oidcProblem)
    startupLog.LogWarning("the identity provider is not being offered: {Problem}", oidcProblem);
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
    McpAvailability mcpAvailability, AlertOptions alertOptions,
    ShareOptions shareOptions, TelemetryOptions telemetry) => Results.Ok(new
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
    // Whether a result can be shared as a link, and whether that link needs a login.
    share = shareOptions.Enabled ? new { isPublic = shareOptions.Public } : null,
    // Where the studio's own traces and metrics go, when they go anywhere.
    telemetry = telemetry.Configured
        ? new { endpoint = telemetry.Endpoint, service = telemetry.ServiceName }
        : null,
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
    // Starting a sign-in can fail before the browser ever reaches the provider: a redirect URI the
    // provider does not know, a pushed-authorization request it refuses. That is a login screen with
    // a message on it, not a 500 with an empty body.
    catch (Exception e) when (ctx.Request.Path.StartsWithSegments("/api/auth/sso")
                             && !ctx.Response.HasStarted)
    {
        startupLog.LogError(e, "the sign-in could not be started");
        ctx.Response.Redirect("/?sso=failed");
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

// Before the guard rather than after it: a refused request is a line worth having, and by here it is
// already known who was refused.
app.UseAuditTrail();

// The whole API needs a signed-in user when credentials are configured. Health and the auth
// endpoints stay open so the SPA can load and log in; without credentials nothing is guarded.
app.Use(async (ctx, next) =>
{
    var users = ctx.RequestServices.GetRequiredService<UserStore>();
    var path = ctx.Request.Path;
    var shares = ctx.RequestServices.GetRequiredService<ShareOptions>();

    var open = !path.StartsWithSegments("/api")
               || path.StartsWithSegments("/api/auth")
               || path.StartsWithSegments("/api/health")
               // A shared result is meant to be openable by whoever has the link — but only when
               // the deployment said so.
               || (shares.Enabled && shares.Public && path.StartsWithSegments("/api/share"));

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
app.MapArchiveEndpoints();
app.MapImportEndpoints();
app.MapDataEndpoints();
app.MapQualityEndpoints();
app.MapIndexTrialEndpoints();
app.MapStorageEndpoints();
app.MapAnalysisEndpoints();
app.MapDdlEndpoints();
app.MapCompareEndpoints();
app.MapAdminEndpoints();
app.MapRedisEndpoints();
app.MapDiagramEndpoints();
app.MapSavedQueryEndpoints();
app.MapReportEndpoints();
app.MapFederationEndpoints();
app.MapAssistantEndpoints();
app.MapShareEndpoints();
app.MapMcpEndpoints();

app.MapMethods("/api/{**rest}", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "PATCH" }, () => Results.NotFound());
app.MapFallbackToFile("index.html").AllowAnonymous();

// Desktop mode: the same server, started from a downloaded binary rather than a container. It shows
// itself once it is listening. In a container there is nothing to show it in.
var shows = (desktop || string.Equals(Environment.GetEnvironmentVariable("WDS_OPEN_BROWSER"), "true",
        StringComparison.OrdinalIgnoreCase))
    && !string.Equals(Environment.GetEnvironmentVariable("WDS_OPEN_BROWSER"), "false",
        StringComparison.OrdinalIgnoreCase);

// A bundled native window was tried here — Photino, over WebView2 on Windows — and taken back out:
// it opened a window that rendered nothing, on a headless session and on a real desktop alike, and a
// dependency that ships native libraries for six platforms has to earn its place by working. What
// does work is asking a browser that is already installed for a window without an address bar.
if (shows)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:8080";

        // An installed Chromium is asked for a window without an address bar, which is what makes a
        // download look like an application; a plain tab is the fallback. WDS_APP_WINDOW=false asks
        // for the tab on purpose.
        var wantsWindow = !string.Equals(app.Configuration["WDS_APP_WINDOW"], "false",
            StringComparison.OrdinalIgnoreCase);

        app.Logger.LogInformation("{What}", wantsWindow
            ? AppWindow.Open(url, ProfilePath(app), app.Logger)
            : OpenTab(url, app.Logger));
    });
}

app.Run();

// Where the browser-in-app-mode window keeps its profile: beside the studio's own data, because it
// belongs to this studio rather than to the browser it borrowed.
static string ProfilePath(WebApplication app) => Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(app.Configuration["DB_PATH"]
        ?? (AppContext.TryGetSwitch("Wds.Desktop", out var isDesktop) && isDesktop
            ? Path.Combine(AppContext.BaseDirectory, "data", "webdatastudio.db")
            : "/data/webdatastudio.db")))!,
    "app-window");

// The plain-tab path, for WDS_APP_WINDOW=false.
static string OpenTab(string url, ILogger logger)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return $"opened {url} in your browser";
    }
    catch (Exception e)
    {
        logger.LogDebug("{Reason}", e.Message);
        return $"open {url} in your browser";
    }
}

public partial class Program { } // exposed for WebApplicationFactory

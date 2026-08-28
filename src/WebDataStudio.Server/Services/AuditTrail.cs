using Microsoft.AspNetCore.Routing.Patterns;

namespace WebDataStudio.Server.Services;

/// Whether the studio writes down what was done through it, and for how long it keeps that.
///
/// On by default: a studio that can drop a table and export a customer list is a studio somebody
/// will eventually be asked questions about. `WDS_AUDIT=false` turns it off for a deployment that
/// keeps its own record, and `WDS_AUDIT_DAYS` decides when old lines are dropped.
public sealed record AuditOptions(bool Enabled, int Days)
{
    public static AuditOptions FromConfiguration(IConfiguration config)
    {
        var enabled = !string.Equals(config["WDS_AUDIT"], "false", StringComparison.OrdinalIgnoreCase);

        return new AuditOptions(enabled,
            int.TryParse(config["WDS_AUDIT_DAYS"], out var days) && days > 0 ? days : 90);
    }
}

/// What an endpoint wants written down beyond its own route.
///
/// The route says "a statement was run"; only the handler knows which one. Bodies are never recorded
/// wholesale — a connection body carries a password — so what lands in the trail is what a handler
/// deliberately says.
public static class Audit
{
    public const string DetailKey = "wds:audit:detail";
    public const string ConnectionKey = "wds:audit:connection";

    /// The longest detail kept. A statement is worth having; a thousand-line migration is worth
    /// having the beginning of.
    public const int MaxDetail = 2000;

    public static void Detail(HttpContext context, string? detail, string? connectionId = null)
    {
        if (detail is { Length: > 0 })
            context.Items[DetailKey] = detail.Length > MaxDetail ? detail[..MaxDetail] : detail;

        if (connectionId is { Length: > 0 }) context.Items[ConnectionKey] = connectionId;
    }

    /// What was called, as the route itself says it: `POST query/execute`, `DELETE storage`.
    ///
    /// The route pattern rather than the URL, so an action is one line in the trail however many
    /// connections and tables it was aimed at, and nothing has to be kept in step with the routing
    /// table by hand.
    public static string Action(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                      ?? context.Request.Path.Value ?? "";

        var route = pattern
            .Replace("/api/", "", StringComparison.Ordinal)
            .Replace("/{conn}", "", StringComparison.Ordinal)
            .Trim('/');

        return $"{context.Request.Method} {route}";
    }

    /// Whether this request is one worth a line.
    ///
    /// Everything that changes something, plus the reads that move data out of the building: an
    /// export and a backup leave with the rows in them, which is the question an audit trail is
    /// usually opened to answer.
    public static bool Interesting(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api")) return false;

        var method = context.Request.Method;

        if (method is not ("GET" or "HEAD" or "OPTIONS")) return true;

        return path.StartsWithSegments("/api/export")
               || path.StartsWithSegments("/api/admin/backup")
               || path.StartsWithSegments("/api/archive")
               || path.StartsWithSegments("/api/storage/download");
    }
}

/// Who did what, through this studio.
///
/// Deliberately not a second logging system: one row per interesting request, in the workspace file
/// next to the query history, readable by an admin and dropped after a while. What it answers is the
/// question a log full of HTTP lines answers badly — "who exported that, and when".
public sealed class AuditTrail(WorkspaceStore workspace, AuditOptions options,
    ILogger<AuditTrail> log)
{
    private int _writes;

    public bool Enabled => options.Enabled && workspace.Available;

    public void Record(AuditEntry entry)
    {
        if (!Enabled) return;

        try
        {
            workspace.AddAudit(entry);

            // Old lines go with the rest of the writing rather than with a timer nobody would think
            // to look for.
            if (Interlocked.Increment(ref _writes) % 200 == 0) workspace.TrimAudit(options.Days);
        }
        catch (Exception e)
        {
            // A trail that cannot be written is worth a line in the log, never a failed request:
            // the export the person asked for has already happened.
            log.LogWarning(e, "could not write the audit trail");
        }
    }

    public IReadOnlyList<AuditEntry> List(string? user, string? connectionId, string? search,
        int limit) =>
        Enabled ? workspace.ListAudit(user, connectionId, search, Math.Clamp(limit, 1, 2000)) : [];
}

/// The one place that writes the trail: after the request, so the status and the duration are known.
public static class AuditMiddleware
{
    public static IApplicationBuilder UseAuditTrail(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var trail = context.RequestServices.GetRequiredService<AuditTrail>();

            if (!trail.Enabled || !Audit.Interesting(context))
            {
                await next();
                return;
            }

            var started = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await next();
            }
            finally
            {
                var user = context.RequestServices.GetRequiredService<CurrentUser>().User;

                trail.Record(new AuditEntry(
                    0,
                    DateTimeOffset.UtcNow,
                    // Nobody signed in is not nobody: an open studio is one person at a machine, and
                    // saying so is better than an empty column.
                    user?.Name ?? "anonymous",
                    user?.Role ?? "",
                    Connection(context),
                    Audit.Action(context),
                    context.Items[Audit.DetailKey] as string ?? "",
                    context.Response.StatusCode,
                    (long)started.Elapsed.TotalMilliseconds,
                    context.Connection.RemoteIpAddress?.ToString() ?? ""));
            }
        });

    /// The connection the request was aimed at: from the route where it is in the route, otherwise
    /// whatever the handler said.
    private static string Connection(HttpContext context) =>
        context.GetRouteValue("conn") as string
        ?? context.Items[Audit.ConnectionKey] as string
        ?? "";
}

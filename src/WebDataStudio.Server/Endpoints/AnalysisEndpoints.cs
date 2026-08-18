using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AnalysisEndpoints
{
    public record AnalyzeQueryRequest(string ConnectionId, string Sql, bool? Actual);

    public static void MapAnalysisEndpoints(this WebApplication app)
    {
        app.MapPost("/api/query/analyze", async (AnalyzeQueryRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);
                await using (session)
                {
                    PlanNode? plan = null;
                    string? planError = null;

                    if (driver.Caps.EstimatedPlan)
                    {
                        try
                        {
                            var mode = body.Actual == true && driver.Caps.ActualPlan
                                ? PlanMode.Actual : PlanMode.Estimated;
                            plan = await driver.ExplainAsync(session, body.Sql, mode, ct);
                        }
                        catch (Exception e)
                        {
                            // A plan the engine refuses is worth reporting, but the SQL-only advice
                            // below still works without it.
                            planError = e.Message;
                        }
                    }

                    var tables = await LoadTablesAsync(driver, session, body.Sql, ct);
                    var findings = new List<AnalyzeFinding>();

                    if (plan is not null) findings.AddRange(PlanRules.Evaluate(plan));
                    findings.AddRange(IndexAdvisor.Suggest(body.Sql, plan, tables, driver.Dialect));

                    return Results.Ok(new
                    {
                        plan,
                        summary = plan is null ? null : PlanSummaryBuilder.Summarize(plan),
                        planError,
                        findings = Deduplicate(findings),
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/analyze/{conn}", async (string conn, string? schema,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = schema is { Length: > 0 }
                        ? new SchemaNodeRef(SchemaNodeKind.Schema, [schema])
                        : null;

                    var report = await driver.AnalyzeAsync(session, AnalyzeScope.Schema, target, ct);
                    return Results.Ok(new { report.Findings });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/stats/{conn}", async (string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.ServerStats)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} does not expose server statistics",
                        });

                    return Results.Ok(await ServerStatistics.ReadAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/stats/{conn}/slow-queries", async (string conn, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.SlowQueryLog)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} does not expose a slow query log",
                        });

                    return Results.Ok(await ServerStatistics.SlowQueriesAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Describes only the tables the statement actually mentions, so the advisor never walks a
    /// whole catalogue to answer one query.
    private static async Task<Dictionary<string, ObjectDetail>> LoadTablesAsync(IDbDriver driver,
        IDbSession session, string sql, CancellationToken ct)
    {
        var wanted = PredicateExtractor.Aliases(sql).Values
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return [];

        var found = new Dictionary<string, ObjectDetail>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<SchemaNodeRef?>();
        queue.Enqueue(null);
        var visited = 0;

        while (queue.Count > 0 && visited++ < 100 && found.Count < wanted.Count)
        {
            var parent = queue.Dequeue();
            IReadOnlyList<SchemaNode> nodes;
            try { nodes = await driver.IntrospectAsync(session, parent, ct); }
            catch (Exception) { continue; }

            foreach (var node in nodes)
            {
                if (node.Ref.Kind == SchemaNodeKind.Table)
                {
                    if (!wanted.Contains(node.Ref.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (found.ContainsKey(node.Ref.Name)) continue;

                    try { found[node.Ref.Name] = await driver.DescribeAsync(session, node.Ref, ct); }
                    catch (Exception) { /* a table we cannot describe simply gets no advice */ }
                    continue;
                }

                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }

        return found;
    }

    private static IReadOnlyList<AnalyzeFinding> Deduplicate(IEnumerable<AnalyzeFinding> findings) =>
        findings
            .GroupBy(f => (f.Category, f.Title), StringComparer.OrdinalIgnoreCase as IEqualityComparer<(string, string)>)
            .Select(g => g.First())
            .OrderBy(f => f.Severity == "critical" ? 0 : f.Severity == "warning" ? 1 : 2)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
}
